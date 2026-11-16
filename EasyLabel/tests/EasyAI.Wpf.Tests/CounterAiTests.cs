using System.Text.Json;
using EasyAI.Desktop.Services;
using EasyAI.Desktop.ViewModels;
using Xunit;

namespace EasyAI.Wpf.Tests
{
    public sealed class CounterAiTests
    {
        [Fact]
        public void ParseEvent_ReadsJsonObjectsAndIgnoresOtherLines()
        {
            Assert.Null(CounterAiRunner.ParseEvent(null));
            Assert.Null(CounterAiRunner.ParseEvent("warning: something"));
            Assert.Null(CounterAiRunner.ParseEvent("{broken"));
            JsonElement? ev = CounterAiRunner.ParseEvent("{\"event\":\"item\",\"name\":\"a.png\",\"count\":238}");
            Assert.NotNull(ev);
            Assert.Equal(238, ev.Value.GetProperty("count").GetInt32());
        }

        [Fact]
        public void BuildStartInfo_DoesNotIsolatePythonSoSiblingModulesImport()
        {
            System.Diagnostics.ProcessStartInfo psi = CounterAiRunner.BuildStartInfo(@"C:\x\counterai_service.py", new[] { "count", "--images", "a b" });
            Assert.DoesNotContain("-I", psi.ArgumentList);
            Assert.Equal(new[] { "-u", @"C:\x\counterai_service.py", "count", "--images", "a b" }, psi.ArgumentList);
        }

        [Fact]
        public void Shared_ThresholdWeightsAndCsvPath()
        {
            Assert.Equal(0.8, CounterAiPaths.ParseThreshold("abc"));
            Assert.Equal(0.8, CounterAiPaths.ParseThreshold(null));
            Assert.Equal(0.8, CounterAiPaths.ParseThreshold("1.5"));
            Assert.Equal(0.65, CounterAiPaths.ParseThreshold("0,65"));
            Assert.Equal(string.Empty, CounterAiPaths.DefaultWeightsPath(null));
            Assert.Equal(System.IO.Path.Combine("p", "counterai", "fastnet.pt"), CounterAiPaths.DefaultWeightsPath("p"));
            Assert.Equal(System.IO.Path.Combine("d", "a_points.csv"), CounterAiPaths.PointsCsvPath(System.IO.Path.Combine("d", "a.png")));
        }

        [Fact]
        public void Labeler_AddRemoveUndoResetAndCsvRoundTrip()
        {
            CounterAiLabeler l = new CounterAiLabeler(43.4);
            l.Load(new[] { (100, 100), (300, 300) });
            l.Add(500.7, 500.2);
            Assert.Equal(3, l.Points.Count);
            Assert.Equal((500, 500), l.Points[2]);
            Assert.False(l.RemoveNear(200, 200));           // farther than reach from both
            Assert.True(l.RemoveNear(110, 95));             // nearest is (100,100)
            Assert.Equal(2, l.Points.Count);
            l.Undo();
            Assert.Equal(3, l.Points.Count);
            l.Reset(new[] { (1, 2) });
            Assert.Single(l.Points);
            l.Undo();
            Assert.Equal(3, l.Points.Count);

            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "counterai-" + System.Guid.NewGuid().ToString("N"), "a_points.csv");
            l.WriteCsv(path);
            Assert.StartsWith("id,x,y", System.IO.File.ReadAllText(path));
            Assert.Equal(l.Points, CounterAiLabeler.ReadCsv(path));
        }

        [Fact]
        public void Engine_ParseResult_ReadsCountAndPointsOrThrowsOnError()
        {
            CounterAiDetection d = CounterAiEngine.ParseResult(JsonDocument.Parse("{\"event\":\"result\",\"id\":1,\"count\":2,\"ms\":12.5,\"points\":[[10,20],[30,40]]}").RootElement);
            Assert.Equal(2, d.Count);
            Assert.Equal((30, 40), d.Points[1]);
            Assert.Equal(12.5, d.Milliseconds);
            Assert.Throws<System.InvalidOperationException>(() => CounterAiEngine.ParseResult(JsonDocument.Parse("{\"event\":\"result\",\"id\":1,\"error\":\"unreadable\"}").RootElement));
        }

        // Real Python round trip; set EASYAI_COUNTERAI_TEST_WEIGHTS and EASYAI_COUNTERAI_TEST_IMAGE to run it.
        [Fact]
        public async System.Threading.Tasks.Task Engine_DetectsThroughPersistentPythonProcess()
        {
            string weights = System.Environment.GetEnvironmentVariable("EASYAI_COUNTERAI_TEST_WEIGHTS");
            string image = System.Environment.GetEnvironmentVariable("EASYAI_COUNTERAI_TEST_IMAGE");
            if (string.IsNullOrEmpty(weights) || string.IsNullOrEmpty(image)) return;
            using (CounterAiEngine engine = await CounterAiEngine.StartAsync(weights, "cpu"))
            {
                CounterAiDetection a = await engine.DetectAsync(image);
                CounterAiDetection b = await engine.DetectAsync(image, 0.5);
                Assert.True(a.Count >= 0 && b.Count >= a.Count);
                Assert.Equal(a.Count, a.Points.Count);
                await Assert.ThrowsAsync<System.InvalidOperationException>(() => engine.DetectAsync(image + ".missing"));
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task ProjectDialog_CounterAiChoice_ReturnsCounterAiKind()
        {
            string location = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "caproj-" + System.Guid.NewGuid().ToString("N"));
            ProjectDialogViewModel vm = new ProjectDialogViewModel(ProjectDialogMode.New, "Wires", location, (a, b) => null, null);
            vm.IsCounterAiKind = true;
            Assert.False(vm.IsDetectKind);
            await vm.OkCommand.ExecuteAsync(null);
            Assert.NotNull(vm.Input);
            Assert.Equal(EasyLabel.Core.Projects.ProjectKind.CounterAi, vm.Input.Kind);
        }

        [Fact]
        public void Bar_RefreshDefaultWeights_FillsExistingFileKeepsUserPathAndFollowsProject()
        {
            string root1 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cabar-" + System.Guid.NewGuid().ToString("N"));
            string root2 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cabar-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root1, "counterai"));
            System.IO.Directory.CreateDirectory(root2);
            string pt = CounterAiPaths.DefaultWeightsPath(root1);
            System.IO.File.WriteAllText(pt, "x");
            string root = root1;
            CanvasViewModel canvas = new CanvasViewModel(System.Windows.Threading.Dispatcher.CurrentDispatcher, (t, m) => { });
            CounterAiBarViewModel bar = new CounterAiBarViewModel(canvas, () => root);
            bar.RefreshDefaultWeights();
            Assert.Equal(pt, bar.WeightsPath);
            root = root2;                              // another project without weights: the auto value is dropped
            bar.RefreshDefaultWeights();
            Assert.Equal(string.Empty, bar.WeightsPath);
            bar.WeightsPath = @"D:\mine\custom.pt";     // typed by the user: never replaced
            root = root1;
            bar.RefreshDefaultWeights();
            Assert.Equal(@"D:\mine\custom.pt", bar.WeightsPath);
            bar.Dispose();
        }

        [Fact]
        public void Bar_ShapeChoiceDrivesCanvasToolAndStampSize()
        {
            CanvasViewModel canvas = new CanvasViewModel(System.Windows.Threading.Dispatcher.CurrentDispatcher, (t, m) => { });
            canvas.Kind = EasyLabel.Core.Projects.ProjectKind.CounterAi;
            Assert.True(canvas.IsStampKind);
            Assert.False(canvas.ShowLabelOnCanvas);   // hundreds of stamps must not each carry a text tag
            CounterAiBarViewModel bar = new CounterAiBarViewModel(canvas, () => null);
            canvas.Tool = EasyLabel.Core.Editing.CanvasTool.Count;
            Assert.True(canvas.IsCountTool);
            bar.IsRectangle = true;
            Assert.False(canvas.StampCircle);
            Assert.Equal(EasyLabel.Core.Editing.CanvasTool.Count, canvas.Tool);
            bar.WidthText = "80";
            bar.HeightText = "50,5";
            Assert.Equal(80, canvas.StampWidth);
            Assert.Equal(50.5, canvas.StampHeight);
            bar.WidthText = "abc";                 // invalid text keeps the last good size
            Assert.Equal(80, canvas.StampWidth);
            bar.IsCircle = true;
            Assert.True(canvas.StampCircle);
            Assert.Equal(string.Empty, bar.ResolvedWeights);
            bar.Dispose();
        }
    }
}

