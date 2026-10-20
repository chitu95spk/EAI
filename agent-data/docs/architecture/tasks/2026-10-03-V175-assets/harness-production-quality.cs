using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BeeCore;
using BeeCore.Func;
using BeeCpp;
using BeeGlobal;
using OpenCvSharp;
using PointF = System.Drawing.PointF;
using RectangleF = System.Drawing.RectangleF;

// Offline-only V175 probe. It deliberately uses Scan/Toggle/Refresh/DoWork through the
// production public seam; it must never call Complete because that can publish externally.
internal static class V175ProductionQualityHarness
{
    private const string DefaultFixtureRoot = @"E:\Data\IK";
    private static string _simSourceId;
    private static readonly string[] FixtureSuffixes =
    {
        "181045057", "181135709", "181217090", "181123605", "181157467", "181243593"
    };

    private enum FrameFormat
    {
        Gray,
        Bgr,
        Bgra
    }

    private sealed class Probe
    {
        public bool Ok;
        public string Summary;
        public bool MapChecked;
        public bool MapNg;
        public float BlurPercent;
        public float HeavyPercent;
        public float FadedPercent;
        public string MapNote;
    }

    private static int Main(string[] args)
    {
        try
        {
            string fixtureRoot = args.Length > 0 ? args[0] : DefaultFixtureRoot;
            string expectedBeeCore = args.Length > 1 ? args[1] : null;
            bool syntheticOnly = args.Length > 2 && string.Equals(
                args[2], "--synthetic-only", StringComparison.OrdinalIgnoreCase);
            bool noRuntimeBoxOnly = args.Length > 2 && string.Equals(
                args[2], "--no-runtime-box-only", StringComparison.OrdinalIgnoreCase);
            if (args.Length > 2 && !syntheticOnly && !noRuntimeBoxOnly)
                throw new ArgumentException("The optional third argument must be --synthetic-only or --no-runtime-box-only.");
            AssertAssemblyIdentity(expectedBeeCore);
            if (noRuntimeBoxOnly)
            {
                Console.WriteLine("PARTIAL V175 no-runtime-box evidence: real fixtures, format parity, publication ABA, and map probes are SKIPPED; this is not overall acceptance.");
                RunNoRuntimeBoxOnly();
                Console.WriteLine("PARTIAL V175 no-runtime-box evidence PASS; real fixture acceptance remains pending.");
                return 0;
            }
            if (syntheticOnly)
            {
                Console.WriteLine("PARTIAL V175 synthetic-only evidence: real fixtures, Gray/BGR/BGRA parity, and fixture-dependent ABA/publication-overlay probes are SKIPPED; this is not overall acceptance.");
                // These groups use only in-memory Mats and the map implementation: disabled-Blur
                // precedence, local contrast, uniform-paper, crisp content, full-pixel denominator,
                // cluster/strict-gate boundaries, clipped edge weighting, and outer-background invariance.
                RunDisabledBlurFadedSynthetic();
                RunR2SampleMapBoundaries();
                Console.WriteLine("PARTIAL V175 synthetic-only evidence PASS; real fixture acceptance remains pending.");
                return 0;
            }
            AssertFixtureIdentity(fixtureRoot);

            RunOriginalGrayFixtures(fixtureRoot);
            RunMultiFixtures(fixtureRoot);
            RunStreakZoneRule(fixtureRoot);
            RunStreakPresets(fixtureRoot);
            RunStreakAi(fixtureRoot);
            RunFrameFormatParity(fixtureRoot);
            RunCacheInvalidation(fixtureRoot);
            RunDisabledBlurFadedSynthetic();
            RunR2SampleMapBoundaries();
            ReportHeldAlgorithmCases();
            Console.WriteLine("V175 production-quality harness PASS for implemented acceptance only");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("V175 production-quality harness FAIL: " + ex);
            return 1;
        }
    }

    private static void RunNoRuntimeBoxOnly()
    {
        MethodInfo singleDecision = typeof(Barcode).GetMethod("IsSingleRuntimeDecision",
            BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo nativeValidity = typeof(Barcode).GetMethod("HasFinitePositiveRuntimeBox",
            BindingFlags.Static | BindingFlags.NonPublic, null, new[] { typeof(RectRotateCli?) }, null);
        MethodInfo multiDecision = typeof(Barcode).GetMethod("EvaluateMulti",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(singleDecision != null && nativeValidity != null && multiDecision != null,
            "V175 no-runtime-box private decision helpers were not found");

        RectRotate saved = RuntimeRect(100f, 100f, 40f, 30f, 0f);
        saved.Name = "expected";
        RectRotate drawOnly = RuntimeRect(100f, 100f, 20f, 20f, 0f);
        RectRotate validPositioning = RuntimeRect(-10f, 3000f, 0.01f, 2f, 15f);
        RectRotate invalidPositioning = RuntimeRect(1f, 1f, 0f, 2f, 0f);
        RectRotate invalidPositioningNan = RuntimeRect(float.NaN, 1f, 1f, 2f, 0f);
        RectRotateCli validFallback = NativeBox(-100f, 4096f, 0.01f, 1f, -10d);
        RectRotateCli invalidZero = NativeBox(1f, 1f, 0f, 1f, 0d);
        RectRotateCli invalidNan = NativeBox(1f, 1f, 1f, float.NaN, 0d);
        RectRotateCli invalidInfinity = NativeBox(1f, 1f, 1f, 1f, double.PositiveInfinity);

        Assert(!(bool)nativeValidity.Invoke(null, new object[] { null }),
            "null native runtime box must be absent");
        Assert(!(bool)nativeValidity.Invoke(null, new object[] { invalidZero }),
            "zero native runtime box must be absent");
        Assert(!(bool)nativeValidity.Invoke(null, new object[] { invalidNan }),
            "NaN native runtime box must be absent");
        Assert(!(bool)nativeValidity.Invoke(null, new object[] { invalidInfinity }),
            "infinite native runtime box must be absent");
        Assert((bool)nativeValidity.Invoke(null, new object[] { validFallback }),
            "thin partly outside native runtime box must be present");

        Barcode single = new Barcode();
        single.rectRotates.Add(drawOnly);
        single.IsOnContent = true;
        single.IsContains = false;
        Assert(!InvokeSingleRuntimeDecision(singleDecision, single, saved, "expected", null, null, null, null),
            "taught or draw geometry must not replace a missing runtime box");
        Assert(!InvokeSingleRuntimeDecision(singleDecision, single, saved, "expected", invalidPositioning, null, invalidZero, invalidNan),
            "all invalid runtime boxes must be NG despite matching content");
        Assert(!InvokeSingleRuntimeDecision(singleDecision, single, saved, "expected", invalidPositioningNan, null, null, null),
            "nonfinite positioning geometry must be absent despite matching content");
        Assert(InvokeSingleRuntimeDecision(singleDecision, single, saved, "expected", validPositioning, null, null, null),
            "valid positioning box must preserve exact matching");
        Assert(InvokeSingleRuntimeDecision(singleDecision, single, saved, "expected", null, validFallback, null, null),
            "valid primary 1D box must preserve exact matching");
        Assert(InvokeSingleRuntimeDecision(singleDecision, single, saved, "expected", null, null, validFallback, null),
            "valid fallback box must preserve exact matching");
        Assert(InvokeSingleRuntimeDecision(singleDecision, single, saved, "expected", null, null, null, validFallback),
            "valid matrix box must preserve exact matching");
        Assert(!InvokeSingleRuntimeDecision(singleDecision, single, saved, "other", validPositioning, null, null, null),
            "exact content mismatch must remain NG");
        Assert(!InvokeSingleRuntimeDecision(singleDecision, single, saved, "", validPositioning, null, null, null),
            "empty content must remain NG");

        single.IsOnContent = false;
        single.IsContains = true;
        single.ContainTemp = "needle";
        Assert(InvokeSingleRuntimeDecision(singleDecision, single, saved, "prefix needle suffix", validPositioning, null, null, null),
            "contains mode must preserve matching with a valid runtime box");
        Assert(!InvokeSingleRuntimeDecision(singleDecision, single, saved, "other", validPositioning, null, null, null),
            "contains mismatch must remain NG");
        single.IsContains = false;
        Assert(InvokeSingleRuntimeDecision(singleDecision, single, saved, "any payload", validPositioning, null, null, null),
            "readable mode must preserve matching with a valid runtime box");

        Barcode multi = new Barcode();
        multi.IsOnContent = false;
        multi.IsContains = false;
        multi.listContentBarcode = new List<string> { "payload" };
        multi.listRectBarcode = null;
        Assert(InvokeMultiDecision(multiDecision, multi) == Results.NG,
            "null Multi runtime rectangles must be NG");
        multi.listRectBarcode = new List<RectRotateCli>();
        Assert(InvokeMultiDecision(multiDecision, multi) == Results.NG,
            "empty Multi runtime rectangles must be NG");
        multi.listRectBarcode = new List<RectRotateCli> { invalidZero, invalidNan, invalidInfinity };
        Assert(InvokeMultiDecision(multiDecision, multi) == Results.NG,
            "all-invalid Multi runtime rectangles must be NG");
        multi.listRectBarcode = new List<RectRotateCli> { invalidZero, validFallback };
        Assert(InvokeMultiDecision(multiDecision, multi) == Results.OK,
            "one valid Multi runtime rectangle must preserve existing payload-count rules");
        multi.IsOnContent = true;
        multi.ListTempBarcode = new List<string> { "payload" };
        Assert(InvokeMultiDecision(multiDecision, multi) == Results.OK,
            "Multi exact matching must remain OK with mixed runtime rectangles");
        multi.IsOnContent = false;
        multi.IsContains = true;
        multi.ContainTemp = "load";
        Assert(InvokeMultiDecision(multiDecision, multi) == Results.OK,
            "Multi contains matching must remain OK with mixed runtime rectangles");
        multi.listContentBarcode = new List<string> { "" };
        Assert(InvokeMultiDecision(multiDecision, multi) == Results.NG,
            "Multi empty content must remain NG with a valid runtime rectangle");

        RunPrimary1DNoPositioningProductionProbe();
    }

    private static bool InvokeSingleRuntimeDecision(
        MethodInfo method, Barcode barcode, RectRotate saved, string content, RectRotate positioning,
        RectRotateCli? primary1D, RectRotateCli? zxing, RectRotateCli? matrix)
    {
        return (bool)method.Invoke(barcode, new object[] { saved, content, positioning, primary1D, zxing, matrix });
    }

    private static Results InvokeMultiDecision(MethodInfo method, Barcode barcode)
    {
        return (Results)method.Invoke(barcode, null);
    }

    // Fixed Code39 test image for the production decoder path. Its high-to-low wide/narrow masks
    // are copied from ZXing.Net Code39Reader lines 40-53: https://raw.githubusercontent.com/micjahn/ZXing.Net/master/Source/lib/oned/Code39Reader.cs.
    // * V 1 7 5 * = 094, 0C1, 121, 025, 130, 094. This is test-only drawing, not an encoder.
    private static void RunPrimary1DNoPositioningProductionProbe()
    {
        Barcode barcode = CreateBarcode();
        barcode.rotCrop = RuntimeRect(202.5f, 120f, 405f, 240f, 0f);
        barcode.rotArea = RuntimeRect(202.5f, 120f, 405f, 240f, 0f);
        barcode.listRotScan = new List<RectRotate>();
        RectRotate taught = RuntimeRect(0f, 0f, 30f, 30f, 0f);
        taught._dragAnchor = AnchorPoint.Center;
        taught.Name = "V175";
        barcode.listRotScan.Add(taught);
        barcode.Is1D = true;
        barcode.IsBox = true;
        barcode.EnablePositionAdjustment = false;
        barcode.IsOnContent = false;
        barcode.IsContains = false;
        barcode.BlurCheckEnabled = false;
        barcode.ContrastFadedEnabled = false;
        barcode.ContrastHeavyEnabled = false;
        Assert(barcode.CurrentPositioningPose == null,
            "primary-1D probe must run with positioning disabled and no saved pose");

        MethodInfo decoder = typeof(Barcode).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "DecodeSingleRoi" && method.GetParameters().Length == 6);
        using (Mat decodeFrame = CreateFixedCode39V175())
        {
            object[] decodeArgs = { decodeFrame, barcode.rotArea, null, null, null, null };
            string content = (string)decoder.Invoke(barcode, decodeArgs);
            RectRotateCli? zxing = (RectRotateCli?)decodeArgs[3];
            RectRotateCli? matrix = (RectRotateCli?)decodeArgs[4];
            RectRotateCli? primary1D = (RectRotateCli?)decodeArgs[5];
            Assert(string.Equals(content, "V175", StringComparison.Ordinal)
                && HasFinitePositiveNativeBox(primary1D)
                && !zxing.HasValue && !matrix.HasValue,
                "primary-1D decoder pass did not return a first-pass runtime rectangle");
        }

        Probe probe = Run(barcode, CreateFixedCode39V175(), "primary-1D positioning-off Code39");
        Assert(probe.Ok,
            "positioning-off primary-1D decode with a valid first-pass rectangle must be OK");
        Probe blank = Run(barcode, CreateBlankCode39Frame(), "primary-1D positioning-off blank");
        Assert(!blank.Ok,
            "blank production frame must be NG after a valid 1D result; no runtime box may be retained");
        Console.WriteLine("primary-1D positioning-off production probe PASS content=V175");
    }

    private static Mat CreateFixedCode39V175()
    {
        const int quietMargin = 60;
        const int frameMargin = 40;
        const int narrow = 3;
        const int wide = 9;
        const int barHeight = 160;
        int[] patterns = { 0x094, 0x0C1, 0x121, 0x025, 0x130, 0x094 };
        int symbolWidth = narrow * 6 + wide * 3;
        int imageWidth = quietMargin * 2 + patterns.Length * symbolWidth
            + (patterns.Length - 1) * narrow;
        Mat image = new Mat(frameMargin * 2 + barHeight, imageWidth, MatType.CV_8UC1, Scalar.All(255));
        int x = quietMargin;
        for (int symbolIndex = 0; symbolIndex < patterns.Length; symbolIndex++)
        {
            for (int bit = 8; bit >= 0; bit--)
            {
                int run = (patterns[symbolIndex] & (1 << bit)) != 0 ? wide : narrow;
                if ((8 - bit) % 2 == 0)
                    Cv2.Rectangle(image, new OpenCvSharp.Rect(x, frameMargin, run, barHeight), Scalar.All(0), -1);
                x += run;
            }
            if (symbolIndex + 1 < patterns.Length)
                x += narrow;
        }
        return image;
    }

    private static Mat CreateBlankCode39Frame()
    {
        Mat image = CreateFixedCode39V175();
        image.SetTo(Scalar.All(255));
        return image;
    }

    private static bool HasFinitePositiveNativeBox(RectRotateCli? box)
    {
        if (!box.HasValue)
            return false;
        RectRotateCli value = box.Value;
        return !float.IsNaN(value.PosCenter.X) && !float.IsInfinity(value.PosCenter.X)
            && !float.IsNaN(value.PosCenter.Y) && !float.IsInfinity(value.PosCenter.Y)
            && !float.IsNaN(value.RectWH.X) && !float.IsInfinity(value.RectWH.X)
            && !float.IsNaN(value.RectWH.Y) && !float.IsInfinity(value.RectWH.Y)
            && !float.IsNaN(value.RectWH.Width) && !float.IsInfinity(value.RectWH.Width)
            && !float.IsNaN(value.RectWH.Height) && !float.IsInfinity(value.RectWH.Height)
            && !double.IsNaN(value.RectRotationDeg) && !double.IsInfinity(value.RectRotationDeg)
            && value.RectWH.Width > 0f && value.RectWH.Height > 0f;
    }

    private static RectRotate RuntimeRect(float x, float y, float width, float height, float angle)
    {
        return new RectRotate(new RectangleF(-width / 2f, -height / 2f, width, height),
            new PointF(x, y), angle, AnchorPoint.None);
    }

    private static RectRotateCli NativeBox(float x, float y, float width, float height, double angle)
    {
        return new RectRotateCli
        {
            Shape = BeeCpp.ShapeType.Rectangle,
            PosCenter = new PointF32(x, y),
            RectWH = new RectF32(-width / 2f, -height / 2f, width, height),
            RectRotationDeg = angle
        };
    }

    private static void RunOriginalGrayFixtures(string fixtureRoot)
    {
        Barcode barcode = Teach(fixtureRoot, "181135709", FrameFormat.Gray);
        string[] accepted = { "181045057", "181135709", "181217090" };
        string[] rejected = { "181123605", "181157467" };
        foreach (string suffix in accepted)
        {
            Probe result = Run(barcode, ReadFixture(fixtureRoot, suffix, FrameFormat.Gray), "gray OK " + suffix);
            Assert(result.MapChecked && !result.MapNg, "map fixture must remain OK: " + suffix);
        }
        foreach (string suffix in rejected)
        {
            Probe result = Run(barcode, ReadFixture(fixtureRoot, suffix, FrameFormat.Gray), "gray NG " + suffix);
            Assert(result.MapChecked && result.MapNg, "map fixture must be NG: " + suffix);
        }
        // V188: the smear fixture has no located Barcode box, so Single is NG on that alone and the quality
        // map is skipped (the taught footprint is not evidence of a code).
        Probe smear = Run(barcode, ReadFixture(fixtureRoot, "181243593", FrameFormat.Gray), "gray smear 181243593");
        Assert(!smear.MapChecked, "smear fixture without a located box must skip the quality map");
    }

    // Multi: every located code is measured against the levels taught from the selected samples.
    private static void RunMultiFixtures(string fixtureRoot)
    {
        Barcode barcode = CreateBarcode();
        barcode.ChangeTeachMode(ModeCheck.Multi);
        ReestablishMultiTeach(barcode, fixtureRoot, "181135709");
        barcode.BlurCheckEnabled = true;
        barcode.ContrastFadedEnabled = true;
        barcode.ContrastHeavyEnabled = true;
        barcode.ContrastFadedMaxPercent = 25f;
        barcode.ContrastHeavyMaxPercent = 25f;
        barcode.QualityAreaLimitPercent = 4f;
        foreach (string suffix in new[] { "181045057", "181135709", "181217090" })
        {
            Probe result = RunMulti(barcode, ReadFixture(fixtureRoot, suffix, FrameFormat.Gray), "multi OK " + suffix);
            Assert(result.MapChecked && !result.MapNg && !MultiQualityNg(barcode),
                "Multi quality must stay OK for fixture " + suffix + ": " + result.Summary);
        }
        foreach (string suffix in new[] { "181123605", "181157467" })
        {
            Probe result = RunMulti(barcode, ReadFixture(fixtureRoot, suffix, FrameFormat.Gray), "multi NG " + suffix);
            Assert(result.MapChecked && result.MapNg && MultiQualityNg(barcode),
                "Multi quality must be NG for fixture " + suffix + ": " + result.Summary);
        }
        // The smeared code cannot be located in Multi: no runtime box, so Multi is NG by the r3 no-box rule
        // (the quality map has nothing to measure and stays unchecked).
        Probe smeared = RunMulti(barcode, ReadFixture(fixtureRoot, "181243593", FrameFormat.Gray), "multi smear");
        Assert(barcode.rectRotates.Count == 0 && !smeared.MapChecked,
            "unlocatable smeared code must leave Multi with no runtime box");
        // Options OFF: the Multi quality map must not run or change the verdict.
        barcode.BlurCheckEnabled = false;
        barcode.ContrastFadedEnabled = false;
        barcode.ContrastHeavyEnabled = false;
        Probe off = RunMulti(barcode, ReadFixture(fixtureRoot, "181123605", FrameFormat.Gray), "multi OFF");
        Assert(!off.MapChecked && !MultiQualityNg(barcode), "Multi quality must be inert when its options are OFF");
    }

    private static void ReestablishMultiTeach(Barcode barcode, string fixtureRoot, string suffix)
    {
        barcode.rotCrop = Area();
        barcode.rotArea = Area();
        using (Mat teach = ReadFixture(fixtureRoot, suffix, FrameFormat.Gray))
        {
            Publish(teach);
            barcode.Scan();
            Assert(barcode.listRotScan != null && barcode.listRotScan.Count > 0, "Multi Scan found no candidate");
            Assert(barcode.ToggleDetectedCandidate(0), "could not select Multi Scan candidate");
            barcode.RefreshSinglePreview();
            float dark = GetPrivateFloat(barcode, "_sampleDarkLevel");
            float light = GetPrivateFloat(barcode, "_sampleLightLevel");
            Console.WriteLine("multi teach " + suffix + " taughtDark=" + dark + " taughtLight=" + light);
            Assert(light - dark >= 5f, "Multi teach did not establish ink/paper levels");
        }
    }

    private static Probe RunMulti(Barcode barcode, Mat frame, string phase)
    {
        using (frame)
        {
            Publish(frame);
            barcode.DoWork(barcode.rotArea, barcode.rotMask);
        }
        Probe probe = ReadQualityProbe(barcode, phase);
        probe.Summary = barcode.LastQualitySummary;
        Console.WriteLine("multi " + phase + " codes=" + (barcode.rectRotates == null ? -1 : barcode.rectRotates.Count)
            + " qualityNg=" + MultiQualityNg(barcode) + " summary=" + probe.Summary);
        return probe;
    }

    private static bool MultiQualityNg(Barcode barcode)
    {
        FieldInfo field = typeof(Barcode).GetField("_multiQualityNg", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(field != null, "private Barcode field was not found: _multiQualityNg");
        return (bool)field.GetValue(barcode);
    }

    // Streak Check judges whole zones of nearby streak lines, not each line; only an NG zone is drawn
    // and listed under the Area Check.
    private static void RunStreakZoneRule(string fixtureRoot)
    {
        Barcode barcode = Teach(fixtureRoot, "181135709", FrameFormat.Gray);
        barcode.BlurCheckEnabled = false;
        barcode.ContrastFadedEnabled = false;
        barcode.ContrastHeavyEnabled = false;
        barcode.StreakCheckEnabled = true;
        barcode.StreakMaxWidthPercent = 15f;
        barcode.StreakMinLengthModules = 3f;
        barcode.StreakMinDepthPercent = 3f;
        barcode.StreakMinCoveragePercent = 35f;

        barcode.StreakMaxLinesPerZone = 2;
        Run(barcode, ReadFixture(fixtureRoot, "181045057", FrameFormat.Gray), "streak zone limit 2");
        Assert(barcode.listRegionOk[0] == false && NgZoneLines(barcode) >= 3,
            "a zone of more than 2 streak lines must make the barcode NG");
        string reasons = string.Join(" | ", InvokeReasons(barcode));
        Assert(reasons.Contains("Streak zone") && reasons.Contains("limit 2"),
            "the NG reasons listed under the Area Check must name the streak zone: " + reasons);

        // Each NG detail can be switched off: the streak zone leaves the list, and the generic
        // "content / count" line must not replace a hidden known cause.
        barcode.SetNgDetailVisible(BeeCore.Algorithm.BarcodeNgDetail.StreakZone, false);
        Assert(InvokeReasons(barcode).Count == 0, "a hidden streak-zone detail must not be listed or replaced");
        barcode.SetNgDetailVisible(BeeCore.Algorithm.BarcodeNgDetail.StreakZone, true);
        Assert(string.Join(" | ", InvokeReasons(barcode)).Contains("Streak zone"),
            "switching the streak-zone detail back on must list it again");
        Assert(barcode.IsNgDetailVisible(BeeCore.Algorithm.BarcodeNgDetail.PrintQuality)
            && barcode.IsNgDetailVisible(BeeCore.Algorithm.BarcodeNgDetail.Content),
            "every NG detail is visible by default");

        barcode.StreakMaxLinesPerZone = 8;
        Run(barcode, ReadFixture(fixtureRoot, "181045057", FrameFormat.Gray), "streak zone limit 8");
        Assert(barcode.listRegionOk[0] && NgZoneLines(barcode) == 0,
            "a zone below the line limit must not make the barcode NG");
        Assert(InvokeReasons(barcode).Count == 0, "an OK barcode must list no NG reasons");

        barcode.StreakMaxLinesPerZone = 0;
        Run(barcode, ReadFixture(fixtureRoot, "181217090", FrameFormat.Gray), "streak zone limit 0 (any line)");
        Assert(NgZoneLines(barcode) >= 1, "limit 0 must keep the old behaviour: any streak line is NG");
    }

    // Streak Check sensitivity levels set every rule; measured on the IKEA DataMatrix fixtures.
    private static void RunStreakPresets(string fixtureRoot)
    {
        Barcode barcode = Teach(fixtureRoot, "181135709", FrameFormat.Gray);
        barcode.BlurCheckEnabled = false;
        barcode.ContrastFadedEnabled = false;
        barcode.ContrastHeavyEnabled = false;
        barcode.StreakCheckEnabled = true;
        Assert(barcode.StreakPresetLevel == BeeCore.Algorithm.StreakPreset.Standard,
            "a fresh Streak Check starts at the Standard level");
        foreach (BeeCore.Algorithm.StreakPreset level in new[] {
            BeeCore.Algorithm.StreakPreset.Sensitive, BeeCore.Algorithm.StreakPreset.Standard, BeeCore.Algorithm.StreakPreset.Strict })
        {
            barcode.ApplyStreakPreset(level);
            string line = level.ToString();
            foreach (string suffix in new[] { "181045057", "181135709", "181217090" })
            {
                Run(barcode, ReadFixture(fixtureRoot, suffix, FrameFormat.Gray), "streak preset " + level + " " + suffix);
                line += " " + suffix + "=" + (NgZoneLines(barcode) > 0 ? "NG(" + NgZoneLines(barcode) + ")" : "ok");
            }
            Console.WriteLine("streak presets: " + line);
        }
        barcode.ApplyStreakPreset(BeeCore.Algorithm.StreakPreset.Standard);
        Run(barcode, ReadFixture(fixtureRoot, "181045057", FrameFormat.Gray), "streak preset standard sag");
        Assert(NgZoneLines(barcode) > 0, "Standard must flag the streaked fixture 181045057");
        Run(barcode, ReadFixture(fixtureRoot, "181217090", FrameFormat.Gray), "streak preset standard clean");
        Assert(NgZoneLines(barcode) == 0, "Standard must not flag the clean fixture 181217090");
        Assert(barcode.StreakMethod == BeeCore.Algorithm.BarcodeStreakMethod.ModuleGrid,
            "a fresh Streak Check starts on the module-grid method");
        barcode.StreakMethod = BeeCore.Algorithm.BarcodeStreakMethod.Classic;
        Run(barcode, ReadFixture(fixtureRoot, "181045057", FrameFormat.Gray), "streak classic method");
        Assert(NgZoneLines(barcode) > 0, "the classic method must still flag the streaked fixture 181045057");
        barcode.StreakMethod = BeeCore.Algorithm.BarcodeStreakMethod.ModuleGrid;
        barcode.ApplyStreakPreset(BeeCore.Algorithm.StreakPreset.Standard);
        barcode.StreakMethod = BeeCore.Algorithm.BarcodeStreakMethod.Classic;
        barcode.StreakMaxLinesPerZone = 0; // any streak line counts: only the pre-filter is under test
        foreach (int level in new[] { 1, 2 })
        {
            barcode.StreakPreFilter = level;
            Run(barcode, ReadFixture(fixtureRoot, "181045057", FrameFormat.Gray), "streak pre-filter " + level);
            Assert(NgZoneLines(barcode) > 0, "the pre-filter (level " + level + ") must still flag the streaked fixture");
        }
        barcode.StreakPreFilter = 0;
        barcode.StreakMethod = BeeCore.Algorithm.BarcodeStreakMethod.ModuleGrid;
        barcode.StreakMinDepthPercent = 7f;
        barcode.MarkStreakCustom();
        Assert(barcode.StreakPresetLevel == BeeCore.Algorithm.StreakPreset.Custom, "a hand edit makes the level Custom");
    }

    // AI method: memory of clean frames, scored on a streaked frame. Detection quality with so few frames is
    // reported, not asserted; the harness checks that the path runs and fails safe without a memory.
    private static void RunStreakAi(string fixtureRoot)
    {
        Barcode barcode = Teach(fixtureRoot, "181135709", FrameFormat.Gray);
        barcode.BlurCheckEnabled = false;
        barcode.ContrastFadedEnabled = false;
        barcode.ContrastHeavyEnabled = false;
        barcode.StreakCheckEnabled = true;
        barcode.StreakMethod = BeeCore.Algorithm.BarcodeStreakMethod.Anomaly;
        barcode.ClearStreakAiBank();
        Assert(!barcode.StreakAiReady && barcode.StreakAiFrames == 0, "a cleared AI memory is empty");
        Run(barcode, ReadFixture(fixtureRoot, "181045057", FrameFormat.Gray), "streak AI without memory");
        Assert(NgZoneLines(barcode) == 0, "without a taught memory the AI method must not report a streak zone");
        foreach (string suffix in new[] { "181217090", "181135709" })
        {
            using (Mat frame = ReadFixture(fixtureRoot, suffix, FrameFormat.Gray))
                Publish(frame);
            string message;
            bool ok = barcode.TeachStreakAiFromCurrentFrame(out message);
            Assert(ok, "teaching the AI memory from " + suffix + " failed: " + message);
        }
        Assert(barcode.StreakAiReady && barcode.StreakAiFrames == 2, "two taught frames make the AI memory ready");
        Run(barcode, ReadFixture(fixtureRoot, "181045057", FrameFormat.Gray), "streak AI with memory");
        Console.WriteLine("streak AI: zones on the streaked frame = " + NgZoneLines(barcode) + " (area percent of the largest in LineCount 0)");
        barcode.ClearStreakAiBank();
        Assert(barcode.StreakAiFrames == 0, "clearing the memory empties it");
    }

    private static int NgZoneLines(Barcode barcode)
    {
        FieldInfo field = typeof(Barcode).GetField("_streakZones", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(field != null, "private Barcode field was not found: _streakZones");
        Array zones = field.GetValue(barcode) as Array;
        int worst = 0;
        if (zones != null)
            foreach (object zone in zones)
            {
                bool defect = (bool)zone.GetType().GetField("IsDefect").GetValue(zone);
                int lines = (int)zone.GetType().GetField("LineCount").GetValue(zone);
                if (defect && lines > worst)
                    worst = lines;
            }
        return worst;
    }

    private static List<string> InvokeReasons(Barcode barcode)
    {
        MethodInfo method = typeof(Barcode).GetMethod("CollectNgReasons", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(method != null, "private Barcode method was not found: CollectNgReasons");
        return (List<string>)method.Invoke(barcode, null);
    }

    private static void RunFrameFormatParity(string fixtureRoot)
    {
        Probe baseline = null;
        foreach (FrameFormat format in new[] { FrameFormat.Gray, FrameFormat.Bgr, FrameFormat.Bgra })
        {
            Barcode barcode = Teach(fixtureRoot, "181135709", format);
            Probe result = Run(barcode, ReadFixture(fixtureRoot, "181123605", format), "format " + format);
            Assert(result.MapChecked && result.MapNg, "map blurred fixture must be NG in " + format);
            Assert(!string.IsNullOrEmpty(result.Summary), "quality map summary is missing in " + format);
            if (baseline == null)
                baseline = result;
            else
            {
                Assert(result.Ok == baseline.Ok, "teach/work verdict differs for " + format);
                Assert(string.Equals(result.Summary, baseline.Summary, StringComparison.Ordinal),
                    "teach/work quality summary differs for " + format);
            }
        }
    }

    private static void RunCacheInvalidation(string fixtureRoot)
    {
        Barcode barcode = Teach(fixtureRoot, "181135709", FrameFormat.Gray);
        Probe initial = Run(barcode, ReadFixture(fixtureRoot, "181123605", FrameFormat.Gray), "cache baseline");
        Assert(!string.IsNullOrEmpty(initial.Summary), "precondition: quality map was not published");
        object oldPublication = CaptureQualityPublication(barcode);
        Assert(DrawQualityMapPixelCount(barcode) > 0, "precondition: current quality map drew no tiles");

        barcode.BlurCheckEnabled = false;
        Assert(string.IsNullOrEmpty(barcode.LastQualitySummary), "Blur toggle retained a cached quality summary");
        AssertLatePublicationSuppressed(barcode, oldPublication, "Blur OFF");
        barcode.BlurCheckEnabled = true;
        Assert(string.IsNullOrEmpty(barcode.LastQualitySummary), "Blur OFF/ON revived a cached quality summary");
        AssertLatePublicationSuppressed(barcode, oldPublication, "Blur OFF/ON ABA");

        Run(barcode, ReadFixture(fixtureRoot, "181123605", FrameFormat.Gray), "cache recompute");
        Assert(!string.IsNullOrEmpty(barcode.LastQualitySummary), "precondition: republished quality map missing");
        object currentPublication = CaptureQualityPublication(barcode);
        Assert(DrawQualityMapPixelCount(barcode) > 0, "current recompute drew no tiles");
        barcode.ChangeTeachMode(ModeCheck.Multi);
        Assert(string.IsNullOrEmpty(barcode.LastQualitySummary), "Single-to-Multi retained a cached quality summary");
        AssertLatePublicationSuppressed(barcode, currentPublication, "Single-to-Multi");
        barcode.ChangeTeachMode(ModeCheck.Single);
        Assert(string.IsNullOrEmpty(barcode.LastQualitySummary), "Single-to-Multi-to-Single revived a cached quality summary");
        AssertLatePublicationSuppressed(barcode, currentPublication, "Single-to-Multi-to-Single ABA");

        ReestablishTeachSelection(barcode, fixtureRoot, "181135709", FrameFormat.Gray);
        Run(barcode, ReadFixture(fixtureRoot, "181123605", FrameFormat.Gray), "cache mode ABA recompute");
        Assert(!string.IsNullOrEmpty(barcode.LastQualitySummary), "current recompute after mode ABA missing");
        Assert(DrawQualityMapPixelCount(barcode) > 0, "current recompute after mode ABA drew no tiles");
    }

    // Reinsert an old private immutable publication to model a worker that completed after an
    // option or mode invalidation. The public summary and private draw path must both reject it.
    private static void AssertLatePublicationSuppressed(Barcode barcode, object oldPublication, string phase)
    {
        FieldInfo field = GetQualityPublicationField();
        field.SetValue(barcode, oldPublication);
        Assert(string.IsNullOrEmpty(barcode.LastQualitySummary),
            phase + " exposed a late quality summary");
        Assert(DrawQualityMapPixelCount(barcode) == 0,
            phase + " drew tiles from a late quality publication");
    }

    private static object CaptureQualityPublication(Barcode barcode)
    {
        object publication = GetQualityPublicationField().GetValue(barcode);
        Assert(publication != null, "quality-map publication was not captured");
        return publication;
    }

    private static FieldInfo GetQualityPublicationField()
    {
        FieldInfo field = typeof(Barcode).GetField("_qualityMapPublication",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(field != null, "quality-map publication field was not found");
        return field;
    }

    private static int DrawQualityMapPixelCount(Barcode barcode)
    {
        MethodInfo draw = typeof(Barcode).GetMethod("DrawQualityMap",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(draw != null, "quality-map draw method was not found");
        Assert(Global.ParaShow != null, "display settings are unavailable");
        bool wasRun = Global.IsRun;
        int opacity = Global.ParaShow.Opacity;
        try
        {
            Global.IsRun = true;
            Global.ParaShow.Opacity = 100;
            using (Bitmap bitmap = new Bitmap(2600, 1600, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                draw.Invoke(barcode, new object[] { graphics, false });
                return CountVisiblePixels(bitmap);
            }
        }
        finally
        {
            Global.IsRun = wasRun;
            Global.ParaShow.Opacity = opacity;
        }
    }

    private static int CountVisiblePixels(Bitmap bitmap)
    {
        Rectangle bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int length = Math.Abs(data.Stride) * bitmap.Height;
            byte[] pixels = new byte[length];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            int count = 0;
            for (int index = 3; index < pixels.Length; index += 4)
                if (pixels[index] != 0)
                    count++;
            return count;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void RunDisabledBlurFadedSynthetic()
    {
        RectRotate area = new RectRotate(
            new RectangleF(-32f, -32f, 64f, 64f), new PointF(32f, 32f), 0f, AnchorPoint.None);
        using (Mat synthetic = new Mat(64, 64, MatType.CV_8UC1))
        {
            for (int y = 0; y < synthetic.Rows; y++)
                for (int x = 0; x < synthetic.Cols; x++)
                    synthetic.Set(y, x, (byte)((x % 5 < 3) ? 110 : 230));

            var fadedOnly = BeeCore.Algorithm.BarcodePrintQualityMap.Analyze(
                synthetic, area, 20f, 230f, false, true, 25f, false, 25f, 4f);
            Assert(fadedOnly.Checked && fadedOnly.FadedPercent > 0f && fadedOnly.Ng,
                "disabled Blur must allow faded classification");

            var blurFirst = BeeCore.Algorithm.BarcodePrintQualityMap.Analyze(
                synthetic, area, 20f, 230f, true, true, 25f, false, 25f, 4f);
            Assert(blurFirst.Checked && blurFirst.BlurPercent > 0f && blurFirst.FadedPercent == 0f,
                "enabled Blur must retain precedence over faded classification");
        }
    }

    // R2 classifier and denominator probes use a 64x64 selected-sample footprint: four 16px
    // tiles are exactly the connected-cluster threshold, and no surrounding Area Check participates.
    private static void RunR2SampleMapBoundaries()
    {
        RectRotate sample = new RectRotate(
            new RectangleF(-32f, -32f, 64f, 64f), new PointF(32f, 32f), 0f, AnchorPoint.None);
        using (Mat paper = new Mat(64, 64, MatType.CV_8UC1, new Scalar(230)))
        {
            var empty = BeeCore.Algorithm.BarcodePrintQualityMap.Analyze(
                paper, sample, 30f, 230f, false, true, 25f, false, 25f, 4f);
            Assert(!empty.Checked && !empty.Ng, "uniform paper must be unchecked, not a fake fade");
        }
        using (Mat faded = new Mat(64, 64, MatType.CV_8UC1, new Scalar(230)))
        {
            FillSampleCrisp(faded, 0, 0);
            FillLocalTiles(faded, 0, 0, 2, 2, 150, 230);
            var result = BeeCore.Algorithm.BarcodePrintQualityMap.Analyze(
                faded, sample, 30f, 230f, false, true, 25f, false, 25f, 4f);
            Assert(result.Checked && result.Ng && result.FadedPixels == 1024 && result.TotalPixels == 4096,
                "30/70 local faded tiles amid sharp content must use exact sample pixels and strict area gating");
        }
        using (Mat heavy = new Mat(64, 64, MatType.CV_8UC1, new Scalar(230)))
        {
            FillSampleCrisp(heavy, 0, 0);
            FillLocalTiles(heavy, 0, 0, 2, 2, 30, 100);
            var result = BeeCore.Algorithm.BarcodePrintQualityMap.Analyze(
                heavy, sample, 30f, 230f, false, false, 25f, true, 25f, 4f);
            Assert(result.Checked && result.Ng && result.HeavyPixels == 1024,
                "30/70 local heavy tiles must not be shadowed by disabled faded classification");
        }
        using (Mat crisp = new Mat(64, 64, MatType.CV_8UC1, new Scalar(230)))
        {
            FillLocalTiles(crisp, 0, 0, 4, 4, 30, 230);
            var result = BeeCore.Algorithm.BarcodePrintQualityMap.Analyze(
                crisp, sample, 30f, 230f, false, true, 25f, true, 25f, 4f);
            Assert(result.Checked && !result.Ng,
                "crisp changed content must not become a contrast defect");
        }
        RunStrictGateAndClippedEdgeBoundaries();
        RunSampleFootprintBackgroundInvariant();
        RunSampleClippedByFrame();
    }

    private static void RunStrictGateAndClippedEdgeBoundaries()
    {
        RectRotate wideSample = new RectRotate(
            new RectangleF(-160f, -40f, 320f, 80f), new PointF(160f, 40f), 0f, AnchorPoint.None);
        using (Mat exactGate = new Mat(80, 320, MatType.CV_8UC1, new Scalar(230)))
        {
            FillLocalTiles(exactGate, 0, 0, 4, 1, 150, 230);
            var result = BeeCore.Algorithm.BarcodePrintQualityMap.Analyze(
                exactGate, wideSample, 30f, 230f, false, true, 25f, false, 25f, 4f);
            Assert(result.Checked && result.FadedPixels == 1024 && result.FadedPercent == 4f && !result.Ng,
                "exact 4% must pass because the default gate is strictly greater-than");
        }
        using (Mat aboveGate = new Mat(80, 320, MatType.CV_8UC1, new Scalar(230)))
        {
            FillLocalTiles(aboveGate, 0, 0, 5, 1, 150, 230);
            var result = BeeCore.Algorithm.BarcodePrintQualityMap.Analyze(
                aboveGate, wideSample, 30f, 230f, false, true, 25f, false, 25f, 4f);
            Assert(result.Checked && result.FadedPixels == 1280 && result.FadedPercent == 5f && result.Ng,
                "area above the default gate must be NG");
        }

        RectRotate edgeSample = new RectRotate(
            new RectangleF(-32.5f, -32f, 65f, 64f), new PointF(32.5f, 32f), 0f, AnchorPoint.None);
        using (Mat edge = new Mat(64, 65, MatType.CV_8UC1, new Scalar(230)))
        {
            FillLocalTiles(edge, 0, 0, 4, 1, 150, 230);
            for (int y = 0; y < 16; y++)
                edge.Set(y, 64, (byte)(y % 5 < 2 ? 150 : 230));
            var result = BeeCore.Algorithm.BarcodePrintQualityMap.Analyze(
                edge, edgeSample, 30f, 230f, false, true, 25f, false, 25f, 4f);
            Assert(result.Checked && result.Columns == 5 && result.TileSize == 16 && result.PatchWidth == 65f
                && result.TotalPixels == 65 * 64 && result.FadedPixels == 1040,
                "clipped right edge must retain its real pixel weight and one-pixel draw width");
        }
    }

    private static void RunSampleFootprintBackgroundInvariant()
    {
        RectRotate sample = new RectRotate(
            new RectangleF(-32f, -32f, 64f, 64f), new PointF(96f, 96f), 0f, AnchorPoint.None);
        using (Mat baseline = new Mat(192, 192, MatType.CV_8UC1, new Scalar(10)))
        using (Mat changedOutside = new Mat(192, 192, MatType.CV_8UC1, new Scalar(250)))
        {
            FillSampleCrisp(baseline, 64, 64);
            FillSampleCrisp(changedOutside, 64, 64);
            var first = BeeCore.Algorithm.BarcodePrintQualityMap.Analyze(
                baseline, sample, 30f, 230f, false, true, 25f, true, 25f, 4f);
            var second = BeeCore.Algorithm.BarcodePrintQualityMap.Analyze(
                changedOutside, sample, 30f, 230f, false, true, 25f, true, 25f, 4f);
            Assert(first.Checked && second.Checked && first.Ng == second.Ng
                && first.FadedPixels == second.FadedPixels && first.HeavyPixels == second.HeavyPixels,
                "selected sample map must ignore changed pixels outside the sample footprint");
        }
    }

    // The selected sample may extend a few pixels past the frame. Only the visible part is measured
    // and the visible area is the denominator; the outside part must neither pass nor dilute it.
    private static void RunSampleClippedByFrame()
    {
        RectRotate sample = new RectRotate(
            new RectangleF(-32f, -32f, 64f, 64f), new PointF(184f, 96f), 0f, AnchorPoint.None);
        using (Mat crisp = new Mat(192, 192, MatType.CV_8UC1, new Scalar(230)))
        using (Mat faded = new Mat(192, 192, MatType.CV_8UC1, new Scalar(230)))
        {
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 40; x++)
                {
                    bool inkCell = ((x / 4 + y / 4) % 2 == 0);
                    crisp.Set(64 + y, 152 + x, (byte)(inkCell ? 30 : 230));
                    faded.Set(64 + y, 152 + x, (byte)(inkCell ? 150 : 230));
                }
            var okResult = BeeCore.Algorithm.BarcodePrintQualityMap.Analyze(
                crisp, sample, 30f, 230f, false, true, 25f, true, 25f, 4f);
            Assert(okResult.Checked && !okResult.Ng && okResult.TotalPixels == 40 * 64,
                "crisp sample clipped by the frame must be checked on its visible area only");
            var ngResult = BeeCore.Algorithm.BarcodePrintQualityMap.Analyze(
                faded, sample, 30f, 230f, false, true, 25f, true, 25f, 4f);
            Assert(ngResult.Checked && ngResult.Ng && ngResult.FadedPercent > 80f,
                "faded visible area must be NG and not diluted by the part outside the frame");
        }
        RectRotate mostlyOutside = new RectRotate(
            new RectangleF(-32f, -32f, 64f, 64f), new PointF(210f, 96f), 0f, AnchorPoint.None);
        using (Mat any = new Mat(192, 192, MatType.CV_8UC1, new Scalar(230)))
        {
            var result = BeeCore.Algorithm.BarcodePrintQualityMap.Analyze(
                any, mostlyOutside, 30f, 230f, false, true, 25f, true, 25f, 4f);
            Assert(!result.Checked && !result.Ng, "a sample mostly outside the frame must stay unchecked");
        }
    }

    private static void FillLocalTiles(Mat image, int startColumn, int startRow, int columns, int rows,
        byte ink, byte paper)
    {
        for (int row = startRow; row < startRow + rows; row++)
            for (int column = startColumn; column < startColumn + columns; column++)
                for (int y = row * 16; y < row * 16 + 16; y++)
                    for (int x = column * 16; x < column * 16 + 16; x++)
                        image.Set(y, x, (x + y * 16) % 10 < 3 ? ink : paper);
    }

    private static void FillSampleCrisp(Mat image, int left, int top)
    {
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                image.Set(top + y, left + x, (byte)(((x / 4 + y / 4) % 2 == 0) ? 30 : 230));
    }

    private static Barcode Teach(string fixtureRoot, string suffix, FrameFormat format)
    {
        Barcode barcode = CreateBarcode();
        ReestablishTeachSelection(barcode, fixtureRoot, suffix, format);
        barcode.BlurCheckEnabled = true;
        barcode.ContrastFadedEnabled = true;
        barcode.ContrastHeavyEnabled = true;
        barcode.ContrastFadedMaxPercent = 25f;
        barcode.ContrastHeavyMaxPercent = 25f;
        barcode.QualityAreaLimitPercent = 4f;
        return barcode;
    }

    // ChangeTeachMode deliberately removes the selected candidate and taught samples. Rebuild
    // them on this same production object through the public Scan -> Toggle -> Refresh path.
    private static void ReestablishTeachSelection(
        Barcode barcode, string fixtureRoot, string suffix, FrameFormat format)
    {
        barcode.rotCrop = Area();
        barcode.rotArea = Area();
        using (Mat teach = ReadFixture(fixtureRoot, suffix, format))
        {
            Publish(teach);
            barcode.Scan();
            Assert(barcode.listRotScan != null && barcode.listRotScan.Count > 0,
                "Scan found no candidate in fixture " + suffix + " (" + format + ")");
            Assert(barcode.ToggleDetectedCandidate(0), "could not select Scan candidate");
            Assert(barcode.listRotScan.Count(item => item != null && item._dragAnchor == AnchorPoint.Center) == 1,
                "expected exactly one selected Barcode sample after ToggleDetectedCandidate");
            barcode.RefreshSinglePreview();
            RectRotate selected = barcode.listRotScan.First(
                item => item != null && item._dragAnchor == AnchorPoint.Center);
            RectRotate selectedWorld = RectRotate.ParentLocalToWorld(selected, barcode.rotArea);
            float taughtDark = GetPrivateFloat(barcode, "_sampleDarkLevel");
            float taughtLight = GetPrivateFloat(barcode, "_sampleLightLevel");
            Console.WriteLine("teach " + suffix + " " + format
                + " selectedLocal=" + DescribeRect(selected)
                + " parent=" + DescribeRect(barcode.rotArea)
                + " selectedWorld=" + DescribeRect(selectedWorld)
                + " taughtDark=" + taughtDark + " taughtLight=" + taughtLight);
            Assert(taughtLight - taughtDark >= 5f,
                "teach did not establish reliable ink/paper levels for " + suffix + " (" + format + ")");
        }
    }

    private static float GetPrivateFloat(Barcode barcode, string fieldName)
    {
        FieldInfo field = typeof(Barcode).GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(field != null, "private Barcode field was not found: " + fieldName);
        return (float)field.GetValue(barcode);
    }

    private static string DescribeRect(RectRotate rect)
    {
        if (rect == null)
            return "<null>";
        return "center=" + rect._PosCenter + " size=" + rect._rect.Width + "x" + rect._rect.Height
            + " angle=" + rect._rectRotation;
    }

    private static Barcode CreateBarcode()
    {
        Barcode barcode = new Barcode { IndexThread = 0, Index = 0 };
        Common.SetToolList(0, new List<PropetyTool>
        {
            new PropetyTool(barcode, TypeTool.BarCode, "V175")
        });
        if (string.IsNullOrEmpty(_simSourceId))
        {
            string error;
            ImageSourceProfile source = ImageSourceRuntimeService.AddSimImageSource(out error);
            Assert(source != null, "could not add SimImage source: " + error);
            _simSourceId = source.Id;
        }
        ImageSourceRuntimeService.SetProgramSourceId(0, _simSourceId);
        return barcode;
    }

    private static Probe Run(Barcode barcode, Mat frame, string phase)
    {
        using (frame)
        {
            Publish(frame);
            barcode.DoWork(barcode.rotArea, barcode.rotMask);
        }
        Assert(barcode.listRegionOk != null && barcode.listRegionOk.Count == 1,
            "DoWork[" + phase + "] published "
                + (barcode.listRegionOk == null ? -1 : barcode.listRegionOk.Count).ToString()
                + " Single results; expected 1");
        Probe probe = ReadQualityProbe(barcode, phase);
        probe.Ok = barcode.listRegionOk[0];
        probe.Summary = barcode.LastQualitySummary;
        return probe;
    }

    private static Probe ReadQualityProbe(Barcode barcode, string phase)
    {
        object publication = GetQualityPublicationField().GetValue(barcode);
        Probe probe = new Probe();
        if (publication == null)
            return probe;
        object map = publication.GetType().GetField("Map", BindingFlags.Instance | BindingFlags.Public).GetValue(publication);
        if (map == null)
            return probe;
        Type mapType = map.GetType();
        probe.MapChecked = (bool)mapType.GetField("Checked").GetValue(map);
        probe.MapNg = (bool)mapType.GetField("Ng").GetValue(map);
        probe.BlurPercent = (float)mapType.GetField("BlurPercent").GetValue(map);
        probe.HeavyPercent = (float)mapType.GetField("HeavyPercent").GetValue(map);
        probe.FadedPercent = (float)mapType.GetField("FadedPercent").GetValue(map);
        probe.MapNote = (string)mapType.GetField("Note").GetValue(map);
        Console.WriteLine("map " + phase + " sample center=" + mapType.GetField("Center").GetValue(map)
            + " size=" + mapType.GetField("PatchWidth").GetValue(map) + "x" + mapType.GetField("PatchHeight").GetValue(map)
            + " angle=" + mapType.GetField("AngleDegrees").GetValue(map)
            + " checked=" + probe.MapChecked + " ng=" + probe.MapNg
            + " blur=" + probe.BlurPercent
            + " heavy=" + probe.HeavyPercent
            + " faded=" + probe.FadedPercent
            + " note=" + (probe.MapNote ?? "<none>"));
        byte[] dumpTiles = mapType.GetField("Tiles").GetValue(map) as byte[];
        int dumpCols = (int)mapType.GetField("Columns").GetValue(map);
        if (dumpTiles != null && dumpCols > 0 && Environment.GetEnvironmentVariable("V175_DUMP_TILES") == "1")
            for (int row = 0; row * dumpCols < dumpTiles.Length; row++)
            {
                char[] line = new char[dumpCols];
                for (int col = 0; col < dumpCols; col++)
                    line[col] = ".BHF"[dumpTiles[row * dumpCols + col] & 3];
                Console.WriteLine("  tiles " + new string(line));
            }
        return probe;
    }

    private static void Publish(Mat frame)
    {
        Assert(ImageSourceRuntimeService.PublishFrameForProgram(frame, 0),
            "could not publish fixture to SimImage runtime");
    }

    private static RectRotate Area()
    {
        return new RectRotate(new RectangleF(-600f, -600f, 1200f, 1200f),
            new PointF(1850f, 880f), 0f, AnchorPoint.None);
    }

    private static Mat ReadFixture(string root, string suffix, FrameFormat format)
    {
        string path = FixturePath(root, suffix);
        ImreadModes mode = format == FrameFormat.Gray ? ImreadModes.Grayscale : ImreadModes.Color;
        Mat frame = Cv2.ImRead(path, mode);
        Assert(frame != null && !frame.Empty(), "could not read fixture: " + path);
        if (format != FrameFormat.Bgra)
            return frame;
        Mat bgra = new Mat();
        try
        {
            Cv2.CvtColor(frame, bgra, ColorConversionCodes.BGR2BGRA);
            return bgra;
        }
        catch
        {
            bgra.Dispose();
            throw;
        }
        finally
        {
            frame.Dispose();
        }
    }

    private static string FixturePath(string root, string suffix)
    {
        string named = Path.Combine(root, "Image_20261002" + suffix + ".bmp");
        if (File.Exists(named))
            return named;
        // The same six frames were later renamed 1..6 in the fixture folder (order of FixtureNumbers).
        int number = Array.IndexOf(FixtureNumbers, suffix) + 1;
        return number > 0 ? Path.Combine(root, number + ".bmp") : named;
    }

    private static readonly string[] FixtureNumbers =
    {
        "181045057", "181123605", "181135709", "181157467", "181217090", "181243593"
    };

    private static void AssertFixtureIdentity(string root)
    {
        foreach (string suffix in FixtureSuffixes)
        {
            string path = FixturePath(root, suffix);
            Assert(File.Exists(path), "missing V175 fixture: " + path);
            Console.WriteLine("fixture " + suffix + " sha256=" + HashFile(path));
        }
    }

    private static void AssertAssemblyIdentity(string expectedBeeCore)
    {
        string actual = Path.GetFullPath(typeof(Barcode).Assembly.Location);
        Assert(File.Exists(actual), "BeeCore assembly location does not exist: " + actual);
        if (!string.IsNullOrEmpty(expectedBeeCore))
            Assert(string.Equals(actual, Path.GetFullPath(expectedBeeCore), StringComparison.OrdinalIgnoreCase),
                "loaded BeeCore assembly differs from the requested isolated assembly");
        Console.WriteLine("BeeCore=" + actual + " sha256=" + HashFile(actual));
    }

    private static string HashFile(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }

    private static void ReportHeldAlgorithmCases()
    {
        Console.WriteLine("NOTE V175 Multi: each located code is measured against levels taught from the selected samples (user-accepted association).");
        Console.WriteLine("UNRESOLVED V175 acceptance: real-fixture classification needs verifier evidence; no threshold retuning is included.");
        Console.WriteLine("UNRESOLVED V175 acceptance: live rotated overlay and UI behavior remain outside this offline harness.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
