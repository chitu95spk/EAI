namespace EasyLabel.Core.Tests
{
    public sealed class AgentChatLogTests : System.IDisposable
    {
        private readonly string _dir = System.IO.Path.Combine(System.AppContext.BaseDirectory, "tmp-chat", System.Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (System.IO.Directory.Exists(_dir)) System.IO.Directory.Delete(_dir, true);
        }

        private string FilePath => System.IO.Path.Combine(_dir, ".easyai", "agent-chat.jsonl");

        private static EasyLabel.Core.Io.AgentChatEntry Make(string prompt, string role = "user", int proposed = 0, decimal? cost = null, int? turns = null, string outcome = "", string error = "")
        {
            return new EasyLabel.Core.Io.AgentChatEntry(
                new System.DateTime(2026, 10, 11, 8, 30, 15, 123, System.DateTimeKind.Utc),
                role, "current", "normal", prompt, new[] { "a/b.png", "c.png" }, proposed, 0, 0, cost, turns, outcome, error);
        }

        [Xunit.Fact]
        public void MissingFileAndNullPathReadAsEmptyAndAppendDoesNotThrow()
        {
            Xunit.Assert.Empty(EasyLabel.Core.Io.AgentChatLog.ReadLast(FilePath));
            Xunit.Assert.Empty(EasyLabel.Core.Io.AgentChatLog.ReadLast(null));
            EasyLabel.Core.Io.AgentChatLog.Append(null, Make("x"));
            EasyLabel.Core.Io.AgentChatLog.Append(FilePath, null);
            Xunit.Assert.False(System.IO.File.Exists(FilePath));
        }

        [Xunit.Fact]
        public void AppendAndReadRoundTrip()
        {
            EasyLabel.Core.Io.AgentChatLog.Append(FilePath, Make("find the pad é", "user"));
            EasyLabel.Core.Io.AgentChatLog.Append(FilePath, Make(string.Empty, "agent", 3, 0.12m, 7, "ok"));

            System.Collections.Generic.IReadOnlyList<EasyLabel.Core.Io.AgentChatEntry> read = EasyLabel.Core.Io.AgentChatLog.ReadLast(FilePath);
            Xunit.Assert.Equal(2, read.Count);
            Xunit.Assert.Equal("user", read[0].Role);
            Xunit.Assert.Equal("find the pad é", read[0].Prompt);
            Xunit.Assert.Equal("current", read[0].Mode);
            Xunit.Assert.Equal("normal", read[0].Effort);
            Xunit.Assert.Equal(new[] { "a/b.png", "c.png" }, read[0].Images);
            Xunit.Assert.Null(read[0].Cost);
            Xunit.Assert.Null(read[0].Turns);
            Xunit.Assert.Equal(System.DateTimeKind.Utc, read[0].Time.Kind);
            Xunit.Assert.Equal(new System.DateTime(2026, 10, 11, 8, 30, 15, 123, System.DateTimeKind.Utc), read[0].Time);
            Xunit.Assert.Equal("agent", read[1].Role);
            Xunit.Assert.Equal(3, read[1].Proposed);
            Xunit.Assert.Equal(0.12m, read[1].Cost);
            Xunit.Assert.Equal(7, read[1].Turns);
            Xunit.Assert.Equal("ok", read[1].Outcome);
            Xunit.Assert.Equal(2, System.IO.File.ReadAllLines(FilePath).Length);
            Xunit.Assert.False(System.IO.File.Exists(FilePath + ".tmp"));
        }

        [Xunit.Fact]
        public void ReadLastReturnsTheNewestEntriesOldestFirst()
        {
            for (int i = 1; i <= 5; i++)
            {
                EasyLabel.Core.Io.AgentChatLog.Append(FilePath, Make("p" + i));
            }

            System.Collections.Generic.IReadOnlyList<EasyLabel.Core.Io.AgentChatEntry> last = EasyLabel.Core.Io.AgentChatLog.ReadLast(FilePath, 3);
            Xunit.Assert.Equal(new[] { "p3", "p4", "p5" }, System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(last, e => e.Prompt)));
            Xunit.Assert.Equal(5, EasyLabel.Core.Io.AgentChatLog.ReadLast(FilePath).Count);
            Xunit.Assert.Empty(EasyLabel.Core.Io.AgentChatLog.ReadLast(FilePath, 0));
        }

        [Xunit.Fact]
        public void TheFileIsTrimmedToTheNewestFiveHundred()
        {
            for (int i = 1; i <= 503; i++)
            {
                EasyLabel.Core.Io.AgentChatLog.Append(FilePath, Make("p" + i));
            }

            Xunit.Assert.Equal(EasyLabel.Core.Io.AgentChatLog.MaxEntries, System.IO.File.ReadAllLines(FilePath).Length);
            System.Collections.Generic.IReadOnlyList<EasyLabel.Core.Io.AgentChatEntry> all = EasyLabel.Core.Io.AgentChatLog.ReadLast(FilePath, 1000);
            Xunit.Assert.Equal(500, all.Count);
            Xunit.Assert.Equal("p4", all[0].Prompt);
            Xunit.Assert.Equal("p503", all[499].Prompt);
        }

        [Xunit.Fact]
        public void CorruptAndPartialLinesAreSkipped()
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            EasyLabel.Core.Io.AgentChatLog.Append(FilePath, Make("good1"));
            System.IO.File.AppendAllText(FilePath, "not json at all\n{\"time\":\"2026-10-11T08:30:15.123Z\"}\n{\"time\":\"bad\",\"role\":\"user\"}\n{\"time\":\"2026-10-11T08:30:15.123Z\",\"role\":\"user\",\"prompt\":\"cut");
            Xunit.Assert.Equal(new[] { "good1" }, System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(EasyLabel.Core.Io.AgentChatLog.ReadLast(FilePath), e => e.Prompt)));

            EasyLabel.Core.Io.AgentChatLog.Append(FilePath, Make("good2"));
            Xunit.Assert.Equal(new[] { "good1", "good2" }, System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(EasyLabel.Core.Io.AgentChatLog.ReadLast(FilePath), e => e.Prompt)));
            Xunit.Assert.Equal(2, System.IO.File.ReadAllLines(FilePath).Length);
        }

        [Xunit.Fact]
        public void ConcurrentAppendsLoseNothing()
        {
            System.Threading.Tasks.Parallel.For(0, 40, i => EasyLabel.Core.Io.AgentChatLog.Append(FilePath, Make("c" + i)));
            System.Collections.Generic.IReadOnlyList<EasyLabel.Core.Io.AgentChatEntry> read = EasyLabel.Core.Io.AgentChatLog.ReadLast(FilePath, 100);
            Xunit.Assert.Equal(40, read.Count);
            Xunit.Assert.Equal(40, System.Linq.Enumerable.Count(System.Linq.Enumerable.Distinct(System.Linq.Enumerable.Select(read, e => e.Prompt))));
        }

        [Xunit.Fact]
        public void ClearEmptiesTheHistoryAndLeavesNoTemporaryFile()
        {
            EasyLabel.Core.Io.AgentChatLog.Append(FilePath, Make("a"));
            EasyLabel.Core.Io.AgentChatLog.Append(FilePath, Make("b"));
            Xunit.Assert.Equal(2, EasyLabel.Core.Io.AgentChatLog.ReadLast(FilePath).Count);

            EasyLabel.Core.Io.AgentChatLog.Clear(FilePath);

            Xunit.Assert.Empty(EasyLabel.Core.Io.AgentChatLog.ReadLast(FilePath));
            Xunit.Assert.False(System.IO.File.Exists(FilePath + ".tmp"));

            EasyLabel.Core.Io.AgentChatLog.Append(FilePath, Make("c"));
            Xunit.Assert.Equal(new[] { "c" }, System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(EasyLabel.Core.Io.AgentChatLog.ReadLast(FilePath), e => e.Prompt)));
        }

        [Xunit.Fact]
        public void ClearToleratesAMissingFileAndAnEmptyPath()
        {
            EasyLabel.Core.Io.AgentChatLog.Clear(FilePath);
            EasyLabel.Core.Io.AgentChatLog.Clear(null);
            EasyLabel.Core.Io.AgentChatLog.Clear("   ");
            Xunit.Assert.False(System.IO.File.Exists(FilePath));
        }

        [Xunit.Fact]
        public void ClearWorksWhileAppendsRunConcurrently()
        {
            for (int i = 0; i < 10; i++)
            {
                EasyLabel.Core.Io.AgentChatLog.Append(FilePath, Make("old" + i));
            }

            System.Threading.Tasks.Parallel.For(0, 30, i =>
            {
                if (i == 15)
                {
                    EasyLabel.Core.Io.AgentChatLog.Clear(FilePath);
                }
                else
                {
                    EasyLabel.Core.Io.AgentChatLog.Append(FilePath, Make("n" + i));
                }
            });

            // Every line left is a whole valid entry (never a half written one) and nothing is duplicated.
            System.Collections.Generic.IReadOnlyList<EasyLabel.Core.Io.AgentChatEntry> read = EasyLabel.Core.Io.AgentChatLog.ReadLast(FilePath, 100);
            Xunit.Assert.Equal(System.IO.File.ReadAllLines(FilePath).Length, read.Count);
            Xunit.Assert.Equal(read.Count, System.Linq.Enumerable.Count(System.Linq.Enumerable.Distinct(System.Linq.Enumerable.Select(read, e => e.Prompt))));
            Xunit.Assert.True(read.Count <= 29);
            Xunit.Assert.False(System.IO.File.Exists(FilePath + ".tmp"));
        }

        [Xunit.Fact]
        public void TheErrorTextIsCappedAndCollapsed()
        {
            string longError = "line one\n\n" + new string('x', 400);
            EasyLabel.Core.Io.AgentChatLog.Append(FilePath, Make(string.Empty, "agent", 0, null, null, "error", longError));
            string stored = EasyLabel.Core.Io.AgentChatLog.ReadLast(FilePath)[0].Error;
            Xunit.Assert.Equal(300, stored.Length);
            Xunit.Assert.StartsWith("line one xxx", stored);
            Xunit.Assert.Equal("a b", EasyLabel.Core.Io.AgentChatLog.LimitError("  a \r\n b  "));
        }

        [Xunit.Fact]
        public void ProjectPathsNamesTheChatFileBesideTheOutcomes()
        {
            EasyLabel.Core.ProjectPaths paths = EasyLabel.Core.ProjectPaths.Legacy(_dir);
            Xunit.Assert.Equal("agent-chat.jsonl", System.IO.Path.GetFileName(paths.AgentChatFile));
            Xunit.Assert.Equal(System.IO.Path.GetDirectoryName(paths.AgentOutcomesFile), System.IO.Path.GetDirectoryName(paths.AgentChatFile));
        }
    }
}
