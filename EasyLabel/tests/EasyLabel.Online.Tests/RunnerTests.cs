using System;
using System.Collections.Generic;
using System.Threading;
using EasyLabel.Online;
using Xunit;

namespace EasyLabel.Online.Tests
{
    public sealed class RunnerTests
    {
        private sealed class FakeHost : IInferenceHost
        {
            public ManualResetEventSlim Gate;
            public int Delay;
            public bool Throw;
            public readonly List<string> Seen = new List<string>();
            public IReadOnlyList<Detection> Infer(OnlineFrame frame, IDictionary<string, double> stageMs)
            {
                lock (Seen) Seen.Add(frame.FrameId);
                if (Gate != null) Gate.Wait();
                if (Delay > 0) Thread.Sleep(Delay);
                if (Throw) throw new InvalidOperationException("boom");
                stageMs["infer"] = Delay;
                return frame.FrameId.StartsWith("ng")
                    ? new List<Detection> { new Detection("scratch", 0.9, new BoundingBox(10, 10, 20, 20), 400, new Centroid(20, 20)) }
                    : new List<Detection>();
            }
        }

        private static OnlineRunner Make(FakeHost host, int capacity, OverloadPolicy policy)
        {
            return new OnlineRunner(host, new RecipeEvaluator(Fixtures.Recipe()), "recipe-1", capacity, policy);
        }

        private static OnlineFrame Frame(string id) { return new OnlineFrame(id, 4, 4, new byte[16]); }

        private static void WaitFor(Func<bool> condition)
        {
            for (int i = 0; i < 400 && !condition(); i++) Thread.Sleep(10);
            Assert.True(condition());
        }

        [Fact]
        public void ProcessesFramesAndReportsVerdictAndStages()
        {
            var host = new FakeHost { Delay = 2 };
            var results = new List<ResultRow>();
            using (var runner = Make(host, 8, OverloadPolicy.DropNewest))
            {
                runner.ResultReady += r => { lock (results) results.Add(r); };
                runner.Start();
                Assert.True(runner.Submit(Frame("ok-1")));
                Assert.True(runner.Submit(Frame("ng-1")));
                WaitFor(() => runner.Metrics.Completed == 2);
                Assert.Equal("OK", results[0].Verdict);
                Assert.Equal("NG", results[1].Verdict);
                var names = new List<string>();
                foreach (var s in runner.Metrics.Snapshot()) names.Add(s.Stage);
                Assert.Contains("queue", names);
                Assert.Contains("infer", names);
                Assert.Contains("evaluate", names);
                Assert.Contains("total", names);
            }
        }

        [Fact]
        public void DropNewestRejectsWhenQueueIsFull()
        {
            var host = new FakeHost { Gate = new ManualResetEventSlim(false) };
            using (var runner = Make(host, 2, OverloadPolicy.DropNewest))
            {
                runner.Start();
                Assert.True(runner.Submit(Frame("a")));
                WaitFor(() => { lock (host.Seen) return host.Seen.Count == 1; });
                Assert.True(runner.Submit(Frame("b")));
                Assert.True(runner.Submit(Frame("c")));
                Assert.False(runner.Submit(Frame("d")));
                Assert.Equal(1, runner.Metrics.Dropped);
                host.Gate.Set();
                WaitFor(() => runner.Metrics.Completed == 3);
                Assert.Equal(new[] { "a", "b", "c" }, host.Seen.ToArray());
            }
        }

        [Fact]
        public void DropOldestKeepsTheFreshestFrames()
        {
            var host = new FakeHost { Gate = new ManualResetEventSlim(false) };
            using (var runner = Make(host, 2, OverloadPolicy.DropOldest))
            {
                runner.Start();
                Assert.True(runner.Submit(Frame("a")));
                WaitFor(() => { lock (host.Seen) return host.Seen.Count == 1; });
                Assert.True(runner.Submit(Frame("b")));
                Assert.True(runner.Submit(Frame("c")));
                Assert.True(runner.Submit(Frame("d")));
                Assert.Equal(1, runner.Metrics.Dropped);
                host.Gate.Set();
                WaitFor(() => runner.Metrics.Completed == 3);
                Assert.Equal(new[] { "a", "c", "d" }, host.Seen.ToArray());
            }
        }

        [Fact]
        public void HostFailureIsCountedAndRunnerKeepsGoing()
        {
            var host = new FakeHost { Throw = true };
            using (var runner = Make(host, 4, OverloadPolicy.DropNewest))
            {
                runner.Start();
                runner.Submit(Frame("x"));
                WaitFor(() => runner.Metrics.Failed == 1);
                host.Throw = false;
                runner.Submit(Frame("y"));
                WaitFor(() => runner.Metrics.Completed == 1);
            }
        }

        [Fact]
        public void StopDrainsQueueAndRejectsFurtherFrames()
        {
            var host = new FakeHost { Delay = 5 };
            var runner = Make(host, 8, OverloadPolicy.DropNewest);
            runner.Start();
            for (int i = 0; i < 5; i++) Assert.True(runner.Submit(Frame("f" + i)));
            runner.Stop();
            Assert.Equal(5, runner.Metrics.Completed);
            Assert.False(runner.IsRunning);
            Assert.False(runner.Submit(Frame("late")));
        }

        [Fact]
        public void PercentilesUseNearestRank()
        {
            var sorted = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            Assert.Equal(5, OnlineMetrics.Percentile(sorted, 0.50));
            Assert.Equal(10, OnlineMetrics.Percentile(sorted, 0.95));
            Assert.Equal(1, OnlineMetrics.Percentile(sorted, 0.0));
        }
    }
}
