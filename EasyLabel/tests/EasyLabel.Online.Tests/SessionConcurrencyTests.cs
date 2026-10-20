using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EasyLabel.Online;
using Xunit;

namespace EasyLabel.Online.Tests
{
    public sealed class SessionConcurrencyTests
    {
        private sealed class Host : IInferenceHost
        {
            public readonly ManualResetEventSlim Entered = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim Gate = new ManualResetEventSlim(false);

            public IReadOnlyList<Detection> Infer(OnlineFrame frame, IDictionary<string, double> stageMs)
            {
                Entered.Set();
                Gate.Wait();
                stageMs["infer"] = 0;
                return new List<Detection>
                {
                    new Detection("scratch", 0.5, new BoundingBox(10, 10, 20, 20), 400, new Centroid(20, 20))
                };
            }
        }

        private static OnlineFrame Frame(string id)
        {
            return new OnlineFrame(id, 4, 4, new byte[16]);
        }

        [Fact]
        public void ThresholdUpdatesWithFramesAndStopRemainBounded()
        {
            Recipe recipe = Fixtures.Recipe();
            var host = new Host();
            var runner = new OnlineRunner(host, new RecipeEvaluator(recipe), "session-concurrency", 128, OverloadPolicy.DropNewest);
            try
            {
                var session = new OnlineSession(runner, recipe, "session-concurrency");
                runner.Start();
                Assert.True(runner.Submit(Frame("inflight")));
                Assert.True(host.Entered.Wait(5000));
                Assert.Null(session.SetThreshold("scratch", 0.2, 0.5));
                var start = new ManualResetEventSlim(false);
                var thresholdResults = new List<string>();
                var thresholdGate = new object();

                Task thresholds = Task.Run(() =>
                {
                    start.Wait();
                    for (int i = 0; i < 32; i++)
                    {
                        string result = session.SetThreshold("scratch", (i & 1) == 0 ? 0.2 : 0.8, 0.5);
                        lock (thresholdGate) thresholdResults.Add(result);
                    }
                });
                Task frames = Task.Run(() =>
                {
                    start.Wait();
                    for (int i = 0; i < 32; i++) runner.Submit(Frame("frame-" + i));
                });
                Task stop = Task.Run(() =>
                {
                    start.Wait();
                    session.Stop();
                });

                start.Set();
                host.Gate.Set();
                Assert.True(Task.WaitAll(new[] { thresholds, frames, stop }, 10000));
                Assert.False(session.IsRunning);
                lock (thresholdGate)
                {
                    Assert.Equal(32, thresholdResults.Count);
                    foreach (string result in thresholdResults) Assert.True(result == null || result == "NOT_RUNNING");
                }
                Assert.False(runner.Submit(Frame("after-stop")));
                Assert.Equal(0, runner.Metrics.Failed);
                start.Dispose();
            }
            finally
            {
                host.Gate.Set();
                runner.Dispose();
                host.Gate.Dispose();
                host.Entered.Dispose();
            }
        }
    }
}
