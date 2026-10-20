using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EasyLabel.Online;
using Xunit;

namespace EasyLabel.Online.Tests
{
    public sealed class RunnerConcurrencyTests
    {
        private sealed class GatedHost : IInferenceHost
        {
            public readonly ManualResetEventSlim Entered = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim Gate = new ManualResetEventSlim(false);

            public IReadOnlyList<Detection> Infer(OnlineFrame frame, IDictionary<string, double> stageMs)
            {
                Entered.Set();
                Gate.Wait();
                stageMs["infer"] = 0;
                return new List<Detection>();
            }
        }

        private static OnlineRunner Make(GatedHost host)
        {
            return new OnlineRunner(host, new RecipeEvaluator(Fixtures.Recipe()), "concurrency", 64, OverloadPolicy.DropNewest);
        }

        private static OnlineFrame Frame(string id)
        {
            return new OnlineFrame(id, 4, 4, new byte[16]);
        }

        [Fact]
        public void StopAndConcurrentSubmittersDrainAcceptedFramesAndCloseAdmission()
        {
            var host = new GatedHost();
            OnlineRunner runner = Make(host);
            try
            {
                runner.Start();
                Assert.True(runner.Submit(Frame("first")));
                Assert.True(host.Entered.Wait(5000));

                var start = new ManualResetEventSlim(false);
                var submitted = new Task[3];
                for (int worker = 0; worker < submitted.Length; worker++)
                {
                    int index = worker;
                    submitted[index] = Task.Run(() =>
                    {
                        start.Wait();
                        for (int i = 0; i < 12; i++) runner.Submit(Frame("submit-" + index + "-" + i));
                    });
                }

                Task stopper = Task.Run(() =>
                {
                    start.Wait();
                    runner.Stop();
                });
                start.Set();
                host.Gate.Set();

                Assert.True(Task.WaitAll(submitted, 10000));
                Assert.True(stopper.Wait(10000));
                Assert.False(runner.IsRunning);
                Assert.False(runner.Submit(Frame("after-stop")));
                Assert.True(runner.Metrics.Accepted >= 1);
                Assert.Equal(runner.Metrics.Accepted, runner.Metrics.Completed);
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
