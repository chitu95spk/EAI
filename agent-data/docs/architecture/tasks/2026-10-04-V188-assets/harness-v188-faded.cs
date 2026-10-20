using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using OpenCvSharp;
using BeeGlobal;
using BeeCore.Algorithm;
using PointF = System.Drawing.PointF;
using RectangleF = System.Drawing.RectangleF;

// V188 harness: taught sample must not be Faded against itself; a synthetic fade must still be caught.
// Args: <teach image> [x,y,w,h] [test image ...]   (box default = whole image; tests default = the teach image)
// A 4-number comma arg is the box; every other extra arg is a test image path.
static class V188
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    static RectRotate Box(Rect r)
    {
        return new RectRotate(new RectangleF(-r.Width / 2f, -r.Height / 2f, r.Width, r.Height),
            new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f), 0f, AnchorPoint.None);
    }

    static bool Levels(Mat g, Rect r, out float dark, out float light)
    {
        var args = new object[] { null, 0f, 0f };
        using (Mat crop = new Mat(g, r))
        {
            args[0] = crop;
            bool ok = (bool)typeof(BeeCore.Barcode).GetMethod("TryMeasureInkLevels", BF).Invoke(null, args);
            dark = (float)args[1]; light = (float)args[2];
            return ok;
        }
    }

    // Otsu dark-class median / light-class median of one tile (harness-side print only).
    static void TileStats(Mat g, Rect r, out float ink, out float paper, out int darkCount)
    {
        using (Mat t = new Mat(g, r)) using (Mat scratch = new Mat())
        {
            int th = (int)Math.Round(Cv2.Threshold(t, scratch, 0, 255, ThresholdTypes.Otsu));
            int[] h = new int[256];
            for (int y = 0; y < t.Rows; y++) for (int x = 0; x < t.Cols; x++) h[t.At<byte>(y, x)]++;
            darkCount = 0; for (int i = 0; i <= th; i++) darkCount += h[i];
            int light = r.Width * r.Height - darkCount;
            ink = Med(h, 0, th, darkCount); paper = Med(h, th + 1, 255, light);
        }
    }
    static float Med(int[] h, int from, int to, int total)
    {
        int target = (total + 1) / 2, c = 0;
        for (int i = from; i <= to; i++) { c += h[i]; if (c >= target) return i; }
        return to;
    }

    // Fraction of the modified top-left quarter's reliable tiles (judged on the ORIGINAL image) that carry the target class.
    static float QuarterFraction(PrintQualityMapResult res, Mat orig, Rect box, float dark, float light, PrintQualityTile target)
    {
        if (!res.Checked || res.Tiles == null) return 0f;
        int qw = box.Width / 2, qh = box.Height / 2, t = res.TileSize, reliable = 0, hit = 0;
        for (int i = 0; i < res.Tiles.Length; i++)
        {
            int c = i % res.Columns, r = i / res.Columns;
            int tw = Math.Min(t, box.Width - c * t), th = Math.Min(t, box.Height - r * t);
            if (c * t + tw / 2 >= qw || r * t + th / 2 >= qh) continue;
            float ink, paper; int dc;
            TileStats(orig, new Rect(box.X + c * t, box.Y + r * t, tw, th), out ink, out paper, out dc);
            if (dc < 24 || paper - ink < 0.3f * (light - dark)) continue;
            reliable++;
            if (res.Tiles[i] == (byte)target) hit++;
        }
        return reliable == 0 ? 0f : 100f * hit / reliable;
    }

    static void Pass(string name, bool ok, string detail) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name + " " + detail); }

    static PrintQualityMapResult Run(Mat g, Rect box, float dark, float light, float refInk, float hInk, float hPaper, string label, bool print)
    {
        var res = BarcodePrintQualityMap.Analyze(g, Box(box), dark, light, true, true, 9f, true, 9f, 5f, refInk, hInk, hPaper);
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0} refs(fadedInk={1:0.0} heavyInk={2:0.0} heavyPaper={3:0.0}) checked={4} faded={5:0.0}% heavy={6:0.0}% blur={7:0.0}% ng={8} {9}",
            label, refInk, hInk, hPaper, res.Checked, res.FadedPercent, res.HeavyPercent, res.BlurPercent, res.Ng, res.Note ?? res.Summary));
        if (print && res.Checked && res.Tiles != null)
            for (int i = 0; i < res.Tiles.Length; i++)
            {
                if (res.Tiles[i] != (byte)PrintQualityTile.Faded) continue;
                int c = i % res.Columns, r = i / res.Columns, t = res.TileSize;
                Rect tr = new Rect(box.X + c * t, box.Y + r * t, Math.Min(t, box.Width - c * t), Math.Min(t, box.Height - r * t));
                float ink, paper; int dc; TileStats(g, tr, out ink, out paper, out dc);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  faded tile col={0} row={1} ink={2:0.0} dark={3:0.0} paper={4:0.0} darkPx={5} (taught light={6:0.0})", c, r, ink, dark, paper, dc, light));
            }
        return res;
    }

    static readonly float FadeStrength = ParseFade();
        static float ParseFade() { float v; string e = Environment.GetEnvironmentVariable("V188_FADE"); return e != null && float.TryParse(e, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : 0.40f; }
        static void Main(string[] a)
    {
        string teach = a[0];
        Mat gt = Cv2.ImRead(teach, ImreadModes.Grayscale);
        Rect box = new Rect(0, 0, gt.Width, gt.Height);
        var tests = new List<string>();
        for (int i = 1; i < a.Length; i++)
        {
            string[] p = a[i].Split(',');
            if (p.Length == 4 && p.All(s => { int v; return int.TryParse(s, out v); }))
            {
                int[] v = p.Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                box = new Rect(v[0], v[1], v[2], v[3]);
            }
            else tests.Add(a[i]);
        }
        if (tests.Count == 0) tests.Add(teach);

        float dark, light;
        if (!Levels(gt, box, out dark, out light)) { Console.WriteLine("TryMeasureInkLevels failed"); return; }
        float refInk, hInk, hPaper;
        bool haveRef = BarcodePrintQualityMap.MeasureFadedReference(gt, Box(box), dark, light, out refInk, out hInk, out hPaper);
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "taught dark={0:0.0} light={1:0.0} contrast={2:0.0} fadedRefInk={3:0.0} heavyRefInk={4:0.0} heavyRefPaper={5:0.0} (ok={6})", dark, light, light - dark, refInk, hInk, hPaper, haveRef));
        float nRef = haveRef ? refInk : 0f, nhInk = haveRef ? hInk : 0f, nhPaper = haveRef ? hPaper : 0f;

        foreach (string f in tests)
        {
            Mat g = f == teach ? gt : Cv2.ImRead(f, ImreadModes.Grayscale);
            Console.WriteLine("== " + f);
            Run(g, box, dark, light, 0f, 0f, 0f, "OLD", true);
            var nr = Run(g, box, dark, light, nRef, nhInk, nhPaper, "NEW", true);
            if (f == teach)
            {
                Pass("taught NEW faded<=4%", nr.Checked && nr.FadedPercent <= 4f, nr.FadedPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%");
                Pass("taught NEW heavy<=1%", nr.Checked && nr.HeavyPercent <= 1f, nr.HeavyPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%");
                Pass("taught NEW blur<=1%", nr.Checked && nr.BlurPercent <= 1f, nr.BlurPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%");
            }
        }

        // Synthetic cases in the top-left quarter of the box: fade = ink lifted toward paper by 25% of the taught contrast;
        // heavy = everything pushed toward ink by 35% of the taught contrast (paper darkens, ink darkens).
        for (int mode = 0; mode < 2; mode++)
        {
            bool fadeCase = mode == 0;
            using (Mat syn = gt.Clone())
            {
                int qw = box.Width / 2, qh = box.Height / 2;
                for (int y = box.Y; y < box.Y + qh; y++)
                    for (int x = box.X; x < box.X + qw; x++)
                    {
                        float p = syn.At<byte>(y, x);
                        float v = fadeCase ? p + FadeStrength * (light - dark) * Math.Max(0f, Math.Min(1f, (light - p) / (light - dark)))
                                           : p - 0.35f * (light - dark);
                        syn.Set<byte>(y, x, (byte)Math.Max(0f, Math.Min(255f, v)));
                    }
                Console.WriteLine(fadeCase ? "== synthetic fade (top-left quarter, +" + (int)Math.Round(FadeStrength * 100) + "% contrast toward paper)" : "== synthetic dark (top-left quarter, -35% contrast)");
                Run(syn, box, dark, light, 0f, 0f, 0f, "OLD", false);
                var res = Run(syn, box, dark, light, nRef, nhInk, nhPaper, "NEW", false);
                PrintQualityTile target = fadeCase ? PrintQualityTile.Faded : PrintQualityTile.Heavy;
                float frac = QuarterFraction(res, gt, box, dark, light, target);
                Pass(fadeCase ? "synthetic fade makes the quality map NG (faded or blur)" : "synthetic dark classified Heavy >=50% of quarter tiles",
                    fadeCase ? res.Ng : frac >= 50f, frac.ToString("0.0", CultureInfo.InvariantCulture) + "% (whole-sample faded=" + res.FadedPercent.ToString("0.0", CultureInfo.InvariantCulture) + "% heavy=" + res.HeavyPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%)");
            }
        }
    }
}
