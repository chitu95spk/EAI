using EasyLabel.Core.Io;

namespace EasyLabel.Core.Tests
{
    public sealed class AgentOutcomeLogTests : System.IDisposable
    {
        private readonly string _dir = System.IO.Path.Combine(System.AppContext.BaseDirectory, "tmp-outcomes", System.Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (System.IO.Directory.Exists(_dir)) System.IO.Directory.Delete(_dir, true);
        }

        private string FilePath => System.IO.Path.Combine(_dir, ".easyai", "agent-outcomes.jsonl");

        private static AgentOutcome Make(string runId, string imageRel = "a/b.png", bool accepted = true, string label = "Pad", double w = 0.1, double h = 0.2)
        {
            return new AgentOutcome(new System.DateTime(2026, 10, 11, 8, 30, 15, 123, System.DateTimeKind.Utc), imageRel, runId, label, "rectangle", "add", accepted, w, h);
        }

        [Xunit.Fact]
        public void MissingFileReadsAsEmpty()
        {
            Xunit.Assert.Empty(AgentOutcomeLog.ReadAll(FilePath));
            Xunit.Assert.Empty(AgentOutcomeLog.ReadAll(null));
        }

        [Xunit.Fact]
        public void AppendAndReadRoundTrip()
        {
            AgentOutcome first = Make("r1", "img/one.png", true, "Pad", 0.125, 0.25);
            AgentOutcome second = Make("r2", "img/two.png", false, "Scratch é", 0.5, 0.0625);
            AgentOutcomeLog.Append(FilePath, first);
            AgentOutcomeLog.AppendMany(FilePath, new[] { second });
            AgentOutcomeLog.AppendMany(FilePath, new AgentOutcome[0]);

            System.Collections.Generic.IReadOnlyList<AgentOutcome> read = AgentOutcomeLog.ReadAll(FilePath);
            Xunit.Assert.Equal(new[] { first, second }, read);
            Xunit.Assert.Equal(System.DateTimeKind.Utc, read[0].Time.Kind);
            Xunit.Assert.False(System.IO.File.Exists(FilePath + ".tmp"));
            Xunit.Assert.Equal(2, System.IO.File.ReadAllLines(FilePath).Length);
        }

        [Xunit.Fact]
        public void TheLineIsOneJsonObjectWithStablePropertyOrder()
        {
            AgentOutcomeLog.Append(FilePath, Make("r1"));
            string line = System.IO.File.ReadAllLines(FilePath)[0];
            Xunit.Assert.Equal(
                "{\"time\":\"2026-10-11T08:30:15.123Z\",\"imageRel\":\"a/b.png\",\"runId\":\"r1\",\"label\":\"Pad\",\"shapeType\":\"rectangle\",\"action\":\"add\",\"accepted\":true,\"widthFraction\":0.1,\"heightFraction\":0.2}",
                line);
        }

        [Xunit.Fact]
        public void CorruptAndPartialLinesAreSkipped()
        {
            AgentOutcomeLog.Append(FilePath, Make("r1"));
            System.IO.File.AppendAllText(FilePath, "not json at all\n[1,2,3]\n{\"label\":\"x\"}\n{\"time\":\"2026-10-11T08:30:15.123Z\",\"label\":\"Pad\",\"accepted\":tru");
            Xunit.Assert.Equal(new[] { "r1" }, AgentOutcomeLog.ReadAll(FilePath).Select(o => o.RunId).ToArray());

            AgentOutcomeLog.Append(FilePath, Make("r2"));
            Xunit.Assert.Equal(new[] { "r1", "r2" }, AgentOutcomeLog.ReadAll(FilePath).Select(o => o.RunId).ToArray());
            Xunit.Assert.Equal(2, System.IO.File.ReadAllLines(FilePath).Length);
        }

        [Xunit.Fact]
        public void TheRecordCapDropsTheOldestRecords()
        {
            AgentOutcomeLog.AppendMany(FilePath, Enumerable.Range(0, AgentOutcomeLog.MaxRecords + 100).Select(i => Make("r" + i)));
            System.Collections.Generic.IReadOnlyList<AgentOutcome> read = AgentOutcomeLog.ReadAll(FilePath);
            Xunit.Assert.Equal(AgentOutcomeLog.MaxRecords, read.Count);
            Xunit.Assert.Equal("r100", read[0].RunId);
            Xunit.Assert.Equal("r" + (AgentOutcomeLog.MaxRecords + 99), read[read.Count - 1].RunId);

            AgentOutcomeLog.Append(FilePath, Make("last"));
            read = AgentOutcomeLog.ReadAll(FilePath);
            Xunit.Assert.Equal(AgentOutcomeLog.MaxRecords, read.Count);
            Xunit.Assert.Equal("r101", read[0].RunId);
            Xunit.Assert.Equal("last", read[read.Count - 1].RunId);
        }

        [Xunit.Fact]
        public void TheByteCapDropsTheOldestRecords()
        {
            string longRel = new string('p', 1000);
            AgentOutcomeLog.AppendMany(FilePath, Enumerable.Range(0, 1000).Select(i => Make("r" + i, longRel)));
            System.Collections.Generic.IReadOnlyList<AgentOutcome> read = AgentOutcomeLog.ReadAll(FilePath);
            Xunit.Assert.True(new System.IO.FileInfo(FilePath).Length <= AgentOutcomeLog.MaxBytes);
            Xunit.Assert.InRange(read.Count, 300, 600);
            Xunit.Assert.Equal("r999", read[read.Count - 1].RunId);
            Xunit.Assert.Equal("r" + (1000 - read.Count), read[0].RunId);
        }

        [Xunit.Fact]
        public void ConcurrentAppendsKeepEveryLineValid()
        {
            const int threads = 4;
            const int perThread = 100;
            System.Threading.Tasks.Task[] tasks = Enumerable.Range(0, threads).Select(t => System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    AgentOutcomeLog.Append(FilePath, Make("t" + t + "-" + i));
                }
            })).ToArray();
            System.Threading.Tasks.Task.WaitAll(tasks);

            string[] lines = System.IO.File.ReadAllLines(FilePath);
            Xunit.Assert.Equal(threads * perThread, lines.Length);
            System.Collections.Generic.IReadOnlyList<AgentOutcome> read = AgentOutcomeLog.ReadAll(FilePath);
            Xunit.Assert.Equal(threads * perThread, read.Count);
            Xunit.Assert.Equal(threads * perThread, read.Select(o => o.RunId).Distinct().Count());
        }

        [Xunit.Fact]
        public void FractionsAreClampedAndLocalTimeIsStoredAsUtc()
        {
            AgentOutcomeLog.Append(FilePath, new AgentOutcome(new System.DateTime(2026, 10, 11, 12, 0, 0, System.DateTimeKind.Local), "a.png", "r", "Pad", "rectangle", "add", true, 2.5, double.NaN));
            AgentOutcome read = AgentOutcomeLog.ReadAll(FilePath).Single();
            Xunit.Assert.Equal(1.0, read.WidthFraction);
            Xunit.Assert.Equal(0.0, read.HeightFraction);
            Xunit.Assert.Equal(new System.DateTime(2026, 10, 11, 12, 0, 0, System.DateTimeKind.Local).ToUniversalTime(), read.Time);
        }

        [Xunit.Fact]
        public void ClearDeletesTheFileAndTheLayoutPutsItNextToTheProjectFile()
        {
            AgentOutcomeLog.Append(FilePath, Make("r1"));
            AgentOutcomeLog.Clear(FilePath);
            Xunit.Assert.False(System.IO.File.Exists(FilePath));
            AgentOutcomeLog.Clear(FilePath);
            Xunit.Assert.Equal(System.IO.Path.Combine(ProjectLayout.MetaDir(_dir), "agent-outcomes.jsonl"), ProjectLayout.AgentOutcomesFile(_dir));
        }
    }
}
