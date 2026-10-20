using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using OpenCvSharp;
using Tune;
using BeeGlobal;
using PointF = System.Drawing.PointF;
using RectangleF = System.Drawing.RectangleF;

static class P
{
    static BeeCore.Barcode barcode = new BeeCore.Barcode();
    static MethodInfo miOpts = typeof(BeeCore.Barcode).GetMethod("CreateDetectOptions", BindingFlags.NonPublic | BindingFlags.Instance);
    static MethodInfo miQuad = typeof(BeeCore.Barcode).GetMethod("QuadWorldOfCliBox", BindingFlags.NonPublic | BindingFlags.Static);
    static PointF[] DecodeQuad(Mat g, bool boxes, out string content, out object sym)
    {
        content = null; sym = null;
        RectRotate roi = new RectRotate(new RectangleF(-g.Width / 2f, -g.Height / 2f, g.Width, g.Height), new PointF(g.Width / 2f, g.Height / 2f), 0f, AnchorPoint.None);
        var rr = BeeCore.Func.Converts.ToCli(roi);
        var o = (BeeCpp.DetectOptionsCli)miOpts.Invoke(barcode, new object[] { boxes });
        List<BeeCpp.RectRotateCli> rects = null; List<string> c = null; List<BeeCpp.CodeSymbologyCli> t = null;
        barcode.BarcodeCoreCli.DetectAll(g.Data, g.Width, g.Height, (int)g.Step(), g.Channels(), rr, null, o, ref rects, ref c, ref t);
        if (c == null) return null;
        for (int i = 0; i < c.Count; i++)
        {
            if (string.IsNullOrEmpty(c[i])) continue;
            content = c[i]; if (t != null && i < t.Count) sym = t[i];
            if (rects != null && i < rects.Count) return (PointF[])miQuad.Invoke(null, new object[] { roi, rects[i] });
            return null;
        }
        return null;
    }
    static float F(string[] a,int i,float d){return a.Length>i?float.Parse(a[i],CultureInfo.InvariantCulture):d;}
    static void Main(string[] a)
    {
        float depth=F(a,0,5), minLen=F(a,1,2), maxW=F(a,2,15);
        BarcodeStreakDetector.MaxGapModules=F(a,3,4); BarcodeStreakDetector.SandwichModules=F(a,4,0.4f); BarcodeStreakDetector.MinVisibility=F(a,5,0.6f);
        var boxes = new Dictionary<string, float[][]>{
          {"1123605", new[]{ new float[]{1316,378,1529,450} }},
          {"1157467", new[]{ new float[]{1528,476,1809,535}, new float[]{1654,621,1893,663} }} };
        foreach (string f in Directory.GetFiles(@"E:\Data\IK", "*.bmp"))
        {
            Mat g = Cv2.ImRead(f, ImreadModes.Grayscale);
            string content; object sym;
            PointF[] q = DecodeQuad(g, false, out content, out sym);
            if (q == null) q = DecodeQuad(g, true, out content, out sym);
            string key = Path.GetFileNameWithoutExtension(f).Substring(Path.GetFileNameWithoutExtension(f).Length - 7);
            if (q == null) { Console.WriteLine(key + " no decode"); continue; }
            BarcodeStreakOptions o = BarcodeStreakOptions.Default;
            o.Direction = BarcodeStreakDirection.Both; o.Polarity = BarcodeStreakPolarity.White; o.MinDepthPercent = depth; o.MinLengthModules = minLen; o.MaxWidthPercentModule = maxW;
            var r = BarcodeStreakDetector.Detect(g, q, o);
            int n = r.Defects == null ? 0 : r.Defects.Count;
            string cover = "";
            float[][] bx; boxes.TryGetValue(key, out bx);
            if (bx != null)
                for (int bi = 0; bi < bx.Length; bi++)
                {
                    bool hit = false;
                    foreach (var s2 in r.Defects)
                    {
                        float yy = (s2.StartWorld.Y + s2.EndWorld.Y) / 2; float xa = Math.Min(s2.StartWorld.X, s2.EndWorld.X), xb = Math.Max(s2.StartWorld.X, s2.EndWorld.X);
                        if (s2.Horizontal && yy >= bx[bi][1] - 10 && yy <= bx[bi][3] + 10 && xa <= bx[bi][2] && xb >= bx[bi][0]) hit = true;
                    }
                    cover += " box" + bi + "=" + (hit ? "HIT" : "miss");
                }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0} defects={1} pitch={2:0.0}{3}", key, n, r.PitchPx, cover));
            if (a.Length > 6 && r.Defects != null)
                foreach (var s2 in r.Defects) Console.WriteLine("    " + BarcodeStreakDetector.Describe(s2) + string.Format(CultureInfo.InvariantCulture, " ({0:0},{1:0})->({2:0},{3:0}) depth={4:0.0} vis={5:0.00}", s2.StartWorld.X, s2.StartWorld.Y, s2.EndWorld.X, s2.EndWorld.Y, s2.DepthPercent, s2.Visibility));
        }
    }
}
