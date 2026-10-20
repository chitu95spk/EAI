using System.Collections.Generic;
using System.Threading;
using EasyLabel.Online;
using Xunit;

namespace EasyLabel.Online.Tests
{
    public sealed class SessionTests
    {
        private sealed class Host : IInferenceHost
        {
            public IReadOnlyList<Detection> Infer(OnlineFrame frame, IDictionary<string, double> stageMs)
            {
                return new List<Detection> { new Detection("scratch", 0.5, new BoundingBox(10, 10, 20, 20), 400, new Centroid(20, 20)) };
            }
        }

        private static string Verdict(OnlineRunner runner, string id)
        {
            string verdict = null;
            var done = new ManualResetEventSlim(false);
            System.Action<ResultRow> handler = r => { if (r.FrameId == id) { verdict = r.Verdict; done.Set(); } };
            runner.ResultReady += handler;
            runner.Submit(new OnlineFrame(id, 2, 2, new byte[4]));
            Assert.True(done.Wait(5000));
            runner.ResultReady -= handler;
            return verdict;
        }

        [Fact]
        public void ThresholdChangeFlipsVerdictWithoutRestart()
        {
            var recipe = Fixtures.Recipe();
            var runner = new OnlineRunner(new Host(), new RecipeEvaluator(recipe), "r1", 4, OverloadPolicy.DropNewest);
            runner.Start();
            using (runner)
            {
                var session = new OnlineSession(runner, recipe, "r1");
                Assert.Equal("NG", Verdict(runner, "a"));
                Assert.Null(session.SetThreshold("scratch", 0.8, 0.5));
                Assert.Equal("OK", Verdict(runner, "b"));
                Assert.Null(session.SetThreshold(null, 0.1, 0.5));
                Assert.Equal("NG", Verdict(runner, "c"));
            }
        }

        [Fact]
        public void RejectsUnknownClassInvalidValuesAndIdleSession()
        {
            var recipe = Fixtures.Recipe();
            var runner = new OnlineRunner(new Host(), new RecipeEvaluator(recipe), "r1", 4, OverloadPolicy.DropNewest);
            var session = new OnlineSession(runner, recipe, "r1");
            Assert.Equal("NOT_RUNNING", session.SetThreshold(null, 0.5, 0.5));
            runner.Start();
            using (runner)
            {
                Assert.Equal("UNKNOWN_CLASS", session.SetThreshold("nope", 0.5, 0.5));
                Assert.Equal("INVALID_THRESHOLD", session.SetThreshold("scratch", 1.5, 0.5));
                Assert.Equal("NG", Verdict(runner, "x"));
            }
        }

        [Fact]
        public void StatusJsonReportsCountersAndStopEndsTheSession()
        {
            var recipe = Fixtures.Recipe();
            var runner = new OnlineRunner(new Host(), new RecipeEvaluator(recipe), "r\"1", 4, OverloadPolicy.DropNewest);
            runner.Start();
            var session = new OnlineSession(runner, recipe, "r\"1");
            Verdict(runner, "a");
            string json = session.StatusJson();
            using (var doc = System.Text.Json.JsonDocument.Parse(json))
            {
                Assert.True(doc.RootElement.GetProperty("running").GetBoolean());
                Assert.Equal("r\"1", doc.RootElement.GetProperty("recipeId").GetString());
                Assert.Equal(1, doc.RootElement.GetProperty("completed").GetInt32());
            }
            session.Stop();
            Assert.False(session.IsRunning);
        }
    }
}
