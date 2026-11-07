using System;
using System.IO;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.ViewModels;
using Xunit;

namespace EasyAI.Wpf.Tests
{
    public sealed class SelfLearnViewModelTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "selflearn-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private string MakeRun(string name)
        {
            string run = Path.Combine(_root, "x.autoloop", name);
            Directory.CreateDirectory(run);
            return run;
        }

        [Fact]
        public void NoFolderShowsNoRun()
        {
            using (SelfLearnViewModel vm = new SelfLearnViewModel(null, null, null))
            {
                Assert.Empty(vm.Rounds);
                Assert.False(vm.CanStop);
                Assert.Contains(Strings.SlStateNone, vm.StateText);
            }
        }

        [Fact]
        public void ReportAndLogOfAFinishedRunAreShown()
        {
            string run = MakeRun("run-20261107-010101");
            File.WriteAllText(Path.Combine(run, "report.json"),
                "{\"target\":{\"map50\":0.95,\"recall\":0.98},\"history\":[{\"round\":0,\"model\":\"yolo11n-obb.pt\",\"synth\":300,\"epochs\":40,\"map50\":0.9,\"recall\":0.8,\"precision\":0.7}],"
                + "\"best\":{\"round\":0,\"map50\":0.9,\"recall\":0.8}}");
            File.WriteAllText(Path.Combine(run, "autoloop.log"), "01:02:03 start\n01:02:09 done. best=None\n");
            using (SelfLearnViewModel vm = new SelfLearnViewModel(null, null, null))
            {
                // the .autoloop folder resolves to its newest run
                vm.RunFolder = Path.GetDirectoryName(run);

                Assert.Single(vm.Rounds);
                Assert.Equal("yolo11n-obb.pt", vm.Rounds[0].Model);
                Assert.Equal("300", vm.Rounds[0].Synth);
                Assert.Contains(Strings.SlStateFinished, vm.StateText);
                Assert.Contains("done.", vm.LogText);
                Assert.False(vm.CanStop);
            }
        }

        [Fact]
        public void LeftoverRunningMarkerWithDeadPidIsNotRunning()
        {
            string run = MakeRun("run-20261107-020202");
            File.WriteAllText(Path.Combine(run, "RUNNING"), "999999");
            File.WriteAllText(Path.Combine(run, "autoloop.log"), "02:02:02 started\n");
            using (SelfLearnViewModel vm = new SelfLearnViewModel(null, null, null))
            {
                vm.RunFolder = run;

                Assert.False(vm.CanStop);
                Assert.Contains(Strings.SlStateFailed, vm.StateText);
            }
        }
    }
}
