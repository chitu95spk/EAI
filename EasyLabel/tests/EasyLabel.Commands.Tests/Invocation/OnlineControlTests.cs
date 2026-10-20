using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using EasyLabel.Commands.Discovery;
using EasyLabel.Commands.Results;
using Xunit;

namespace EasyLabel.Commands.Tests.Invocation
{
    public sealed class OnlineControlTests
    {
        private sealed class FakeControl : IOnlineControl
        {
            public bool Running = true;
            public int Stops;
            public string? LastClass;
            public double LastScore, LastNms;
            public string? Error;
            public bool IsRunning { get { return Running; } }
            public void Stop() { Stops++; Running = false; }
            public string StatusJson() { return "{\"running\":" + (Running ? "true" : "false") + ",\"completed\":3}"; }
            public string? SetThreshold(string? className, double score, double nms)
            {
                LastClass = className; LastScore = score; LastNms = nms;
                return Error;
            }
        }

        private static JsonElement Args(string json)
        {
            using (JsonDocument document = JsonDocument.Parse(json)) return document.RootElement.Clone();
        }

        private static async Task<CommandResult> Run(FakeControl control, string id, string args, CtxFlags flags, RunMode mode)
        {
            var scanner = new CommandScanner();
            var catalog = scanner.Scan(new OnlineCommandDescriptors(control));
            var registry = new CommandRegistry();
            scanner.Register(registry, catalog);
            return await new CommandInvoker(registry).ExecuteAsync(new CommandId(id), Args(args), new CommandContext(flags, mode));
        }

        [Fact]
        public async Task StopStopsARunningSession()
        {
            var control = new FakeControl();
            CommandResult result = await Run(control, "ONL.STOP", "{}", CtxFlags.OnlineRunning, RunMode.Online);
            Assert.True(result.Ok);
            Assert.Equal(1, control.Stops);
        }

        [Fact]
        public async Task StopWhenIdleFailsWithNotRunning()
        {
            var control = new FakeControl { Running = false };
            CommandResult result = await Run(control, "ONL.STOP", "{}", CtxFlags.None, RunMode.Online);
            Assert.False(result.Ok);
            Assert.Equal("NOT_RUNNING", result.Code);
            Assert.Equal(0, control.Stops);
        }

        [Fact]
        public async Task StatusReturnsTheSessionJson()
        {
            CommandResult result = await Run(new FakeControl(), "ONL.STATUS", "{}", CtxFlags.None, RunMode.Online);
            Assert.True(result.Ok);
            Assert.Equal(3, result.Data!.Value.GetProperty("completed").GetInt32());
        }

        [Fact]
        public async Task ThresholdPassesClassScoreAndNmsWhileRunning()
        {
            var control = new FakeControl();
            CommandResult result = await Run(control, "ONL.THRESHOLD.SET", "{\"class\":\"scratch\",\"score\":0.6,\"nms\":0.4}",
                CtxFlags.OnlineRunning, RunMode.Online);
            Assert.True(result.Ok);
            Assert.Equal("scratch", control.LastClass);
            Assert.Equal(0.6, control.LastScore);
            Assert.Equal(0.4, control.LastNms);
        }

        [Fact]
        public async Task ThresholdSurfacesControlErrorsAndRefusesWhenIdle()
        {
            var control = new FakeControl { Error = "UNKNOWN_CLASS" };
            CommandResult bad = await Run(control, "ONL.THRESHOLD.SET", "{\"class\":\"x\",\"score\":0.6,\"nms\":0.4}",
                CtxFlags.OnlineRunning, RunMode.Online);
            Assert.False(bad.Ok);
            Assert.Equal("UNKNOWN_CLASS", bad.Code);

            var idle = new FakeControl { Running = false };
            CommandResult none = await Run(idle, "ONL.THRESHOLD.SET", "{\"score\":0.6,\"nms\":0.4}", CtxFlags.None, RunMode.Online);
            Assert.Equal("NOT_RUNNING", none.Code);
            Assert.Null(idle.LastClass);
        }

        [Fact]
        public void WithoutControlTheThreeCommandsStayReserved()
        {
            var catalog = new CommandScanner().Scan(new OnlineCommandDescriptors());
            Assert.Equal(14, catalog.Count);
            Assert.Equal(14, new CommandScanner().Scan(new OnlineCommandDescriptors(new FakeControl())).Count);
        }
    }
}
