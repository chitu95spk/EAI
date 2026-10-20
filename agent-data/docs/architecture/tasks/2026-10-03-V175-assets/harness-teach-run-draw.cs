using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using OpenCvSharp;
using BeeGlobal;
using PointF = System.Drawing.PointF;
using RectangleF = System.Drawing.RectangleF;

static class P5
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static T Get<T>(object o, string n) { return (T)o.GetType().GetField(n, BF).GetValue(o); }
    static RectRotate Area() { return new RectRotate(new RectangleF(-600, -600, 1200, 1200), new PointF(1850, 880), 0f, AnchorPoint.None); }
    static RectRotate DecodeBox(BeeCore.Barcode b, Mat g)
    {
        var args = new object[] { g, Area(), null, null, null };
        var t = typeof(BeeCore.Barcode);
        string c = (string)t.GetMethod("DecodeSingleRoi", BF, null, new Type[] { typeof(Mat), typeof(RectRotate), typeof(BeeCpp.CodeSymbologyCli).MakeByRefType(), typeof(BeeCpp.RectRotateCli?).MakeByRefType(), typeof(BeeCpp.RectRotateCli?).MakeByRefType() }, null).Invoke(b, args);
        if (string.IsNullOrEmpty(c) || args[4] == null) return null;
        object local = t.GetMethod("FromCliLocal", BF).Invoke(null, new object[] { args[4], 0f });
        return (RectRotate)t.GetMethod("AreaLimitToWorld", BF).Invoke(null, new object[] { Area(), local });
    }
    static void Main(string[] a)
    {
        string teach = a[0];
        float lim = a.Length > 1 ? float.Parse(a[1], CultureInfo.InvariantCulture) : 25f;
        bool pos = a.Length > 2 && a[2] == "pos";
        var T = typeof(BeeCore.Barcode);
        var b = new BeeCore.Barcode();
        Mat gt = Cv2.ImRead(@"E:\Data\IK\Image_2026100218" + teach + ".bmp", ImreadModes.Grayscale);
        // Production teach path: locator candidate -> selected (local to the Area Check) -> RebuildSinglePreview.
        var area = Area();
        T.GetField("rotArea", BF).SetValue(b, area);
        var det = T.GetMethod("DetectCandidates", BF, null, new Type[] { typeof(Mat), typeof(RectRotate), typeof(RectRotate), typeof(bool) }, null);
        object cands = det.Invoke(b, new object[] { gt, area, null, true });
        var rects = (System.Collections.IList)cands.GetType().GetField("Rectangles").GetValue(cands);
        if (rects.Count == 0) { Console.WriteLine("no candidate"); return; }
        RectRotate local = (RectRotate)T.GetMethod("BuildCandidateLocal", BF).Invoke(null, new object[] { rects[0] });
        local._dragAnchor = AnchorPoint.Center;
        T.GetField("listRotScan", BF).SetValue(b, new List<RectRotate> { local });
        T.GetMethod("RebuildSinglePreview", BF).Invoke(b, new object[] { gt, area });
        Console.WriteLine("levels dark=" + Get<float>(b, "_sampleDarkLevel") + " light=" + Get<float>(b, "_sampleLightLevel")); Console.WriteLine("teach=" + teach + " sample contrast=" + Get<float>(b, "_contrastSample").ToString("0.0", CultureInfo.InvariantCulture) + " blurMask=" + (Get<byte[]>(b, "_blurSampleMask") != null));
        b.ContrastFadedMaxPercent = lim; b.ContrastHeavyMaxPercent = lim;
        if (pos)
        {
            b.EnablePositionAdjustment = true;
            T.GetField("_positioningBoxIndex", BF).SetValue(b, 0);
            T.GetField("_positioningReference", BF).SetValue(b, new RectRotate(new RectangleF(-395, -390, 790, 780), new PointF(1900, 870), 0f, AnchorPoint.None));
        }
        var work = T.GetMethod("DoWorkSingle", BF);
        b.BlurCheckEnabled = true;
        foreach (var mode in new[] { "OFF", "faded", "heavy", "both" })
        {
            b.ContrastFadedEnabled = mode == "faded" || mode == "both"; b.ContrastHeavyEnabled = mode == "heavy" || mode == "both";
            b.BlurCheckEnabled = mode != "OFF";
            foreach (string f in Directory.GetFiles(@"E:\Data\IK", "*.bmp"))
            {
                Mat g = Cv2.ImRead(f, ImreadModes.Grayscale);
                T.GetField("listRegionOk", BF).SetValue(b, new List<bool>()); T.GetField("listRegionDetected", BF).SetValue(b, new List<string>());
                if (pos) { var args = new object[] { g, null, null, null }; bool okp = (bool)T.GetMethod("TryLocatePositioningPose").Invoke(b, args); }
                work.Invoke(b, new object[] { g, Area() });
                var ok = Get<List<bool>>(b, "listRegionOk");
                string nm = Path.GetFileNameWithoutExtension(f); nm = nm.Substring(nm.Length - 7);
                Console.WriteLine(string.Format("{0,-6} {1} regionOk={2} contrastDev={3:0.0}% quality={4}", mode, nm, string.Join(",", ok), b.LastContrastDeviationPercent, b.LastQualitySummary));
                if (mode == "both")
                {
                    using (var bmp = new System.Drawing.Bitmap(g.Width, g.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        using (var gc = System.Drawing.Graphics.FromImage(bmp))
                        using (var src = OpenCvSharp.Extensions.BitmapConverter.ToBitmap(g))
                        {
                            gc.DrawImage(src, 0, 0, g.Width, g.Height);
                            Global.IsRun = true; Global.ScaleZoom = 1f; Global.ParaShow.Opacity = 45;
                            Global.ParaShow.ColorNG = System.Drawing.Color.Red; Global.ParaShow.ThicknessLine = 3; Global.ParaShow.FontSize = 20;
                            T.GetMethod("DrawQualityMap", BF).Invoke(b, new object[] { gc, false });
                            T.GetMethod("DrawContrastStatus", BF).Invoke(b, new object[] { gc, false });
                        }
                        bmp.Save("qmap_" + nm + ".png");
                    }
                }
            }
        }
    }
}
