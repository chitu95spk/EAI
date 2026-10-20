using EasyLabel.Core.Io;

namespace EasyLabel.Core.Tests
{
    public sealed class AgentLearningCoreTests : System.IDisposable
    {
        private readonly string _dir = System.IO.Path.Combine(System.AppContext.BaseDirectory, "tmp-learning-core", System.Guid.NewGuid().ToString("N"));

        private static readonly System.DateTime Now = new System.DateTime(2026, 10, 12, 8, 0, 0, System.DateTimeKind.Utc);

        public void Dispose()
        {
            if (System.IO.Directory.Exists(_dir)) System.IO.Directory.Delete(_dir, true);
        }

        private string VocabularyPath => System.IO.Path.Combine(_dir, ".easyai", "vocabulary.json");

        private static AgentOutcome Review(string source, string label, bool accepted)
        {
            return new AgentOutcome(Now, "a.png", "", label, "rectangle", "add", accepted, 0.2, 0.1, source);
        }

        private static AgentOutcome Edit(string label, float areaRatio, string? relabelTo = null)
        {
            return new AgentOutcome(Now, "a.png", "", label, "rectangle", "edit", true, 0.2, 0.1, "Agent", 0.7f, relabelTo, areaRatio);
        }

        private static System.Collections.Generic.List<AgentOutcome> MixedRecords()
        {
            System.Collections.Generic.List<AgentOutcome> list = new System.Collections.Generic.List<AgentOutcome>();
            for (int i = 0; i < 10; i++) list.Add(Review("Agent", "NG", i < 6));
            for (int i = 0; i < 6; i++) list.Add(Review("SegAI", "Pad", i == 0));
            for (int i = 0; i < 4; i++) list.Add(Edit("NG", 0.8f));
            list.Add(Edit("NG", 0.8f, "OK"));
            list.Add(Edit("NG", 0.8f, "OK"));
            return list;
        }

        // ---------- digest ----------

        [Xunit.Fact]
        public void MixedSourceDigestIsDeterministicAndWithinTheCap()
        {
            string first = AgentOutcomeDigest.Build(MixedRecords());
            string second = AgentOutcomeDigest.Build(MixedRecords());

            Xunit.Assert.Equal(first, second);
            Xunit.Assert.True(first.Length <= 1200);
            Xunit.Assert.Contains("- NG: accept rate ~60% (some reviews)", first);
            Xunit.Assert.Contains("- SegAI over-proposes label Pad (rejected 80%)", first);
            Xunit.Assert.Contains("- Accepted shapes usually shrink by ~20% in area when the user edits them", first);
            Xunit.Assert.Contains("- Often relabelled after accepting: NG -> OK", first);

            for (int cap = 60; cap < 400; cap += 37)
            {
                Xunit.Assert.True(AgentOutcomeDigest.Build(MixedRecords(), cap).Length <= cap);
            }
        }

        [Xunit.Fact]
        public void AgentOnlyHistoryHasNoCrossSourceLines()
        {
            System.Collections.Generic.List<AgentOutcome> list = new System.Collections.Generic.List<AgentOutcome>();
            for (int i = 0; i < 6; i++) list.Add(Review("Agent", "NG", i < 3));

            string digest = AgentOutcomeDigest.Build(list);
            Xunit.Assert.Contains("- NG: accept rate ~50% (few reviews)", digest);
            Xunit.Assert.DoesNotContain("Other suggestion sources", digest);
        }

        [Xunit.Fact]
        public void EditRecordsDoNotCountAsReviews()
        {
            System.Collections.Generic.List<AgentOutcome> list = new System.Collections.Generic.List<AgentOutcome>();
            for (int i = 0; i < 5; i++) list.Add(Review("Agent", "NG", true));
            for (int i = 0; i < 5; i++) list.Add(Edit("NG", 1.0f));

            Xunit.Assert.Contains("- NG: accept rate ~100% (few reviews)", AgentOutcomeDigest.Build(list));
        }

        [Xunit.Fact]
        public void OtherSourcesAloneStillGiveAShortDigest()
        {
            System.Collections.Generic.List<AgentOutcome> list = new System.Collections.Generic.List<AgentOutcome>();
            for (int i = 0; i < 5; i++) list.Add(Review("YOLO", "Fan", false));

            string digest = AgentOutcomeDigest.Build(list);
            Xunit.Assert.Contains("- YOLO over-proposes label Fan (rejected 100%)", digest);
            Xunit.Assert.DoesNotContain("accept rate", digest);
        }

        [Xunit.Fact]
        public void OnlyOneRecordPerSourceAndLabelIsBelowTheThreshold()
        {
            System.Collections.Generic.List<AgentOutcome> list = new System.Collections.Generic.List<AgentOutcome>();
            for (int i = 0; i < 5; i++) list.Add(Review("SAM2", "Fan" + i, false));

            Xunit.Assert.Equal(string.Empty, AgentOutcomeDigest.Build(list));
        }

        // ---------- outcome source ----------

        [Xunit.Fact]
        public void UnknownSourceTextIsOtherAndMissingIsAgent()
        {
            Xunit.Assert.Equal("Agent", OutcomeSources.Normalize(null));
            Xunit.Assert.Equal("Agent", OutcomeSources.Normalize(" "));
            Xunit.Assert.Equal("SegAI", OutcomeSources.Normalize("segai"));
            Xunit.Assert.Equal("Other", OutcomeSources.Normalize("Whatever"));
        }

        // ---------- vocabulary ----------

        [Xunit.Fact]
        public void TermsAreLowerCaseWordsWithoutStopwordsShortWordsAndNumbers()
        {
            System.Collections.Generic.IReadOnlyList<string> terms = ProjectVocabulary.TermsOf("Please mark ALL the QUAT fans, 12 and quat ab");
            Xunit.Assert.Equal(new[] { "quat", "fans" }, terms);
            Xunit.Assert.Empty(ProjectVocabulary.TermsOf(null));
        }

        [Xunit.Fact]
        public void WordIsLearnedOnlyFromTheThirdRun()
        {
            ProjectVocabulary.Learn(VocabularyPath, "label the quat", new[] { "FAN" }, Now);
            ProjectVocabulary.Learn(VocabularyPath, "find quat", new[] { "FAN" }, Now);
            Xunit.Assert.Empty(ProjectVocabulary.Learned(VocabularyPath));
            Xunit.Assert.Equal(string.Empty, ProjectVocabulary.Block(VocabularyPath));

            ProjectVocabulary.Learn(VocabularyPath, "quat please", new[] { "FAN" }, Now);
            System.Collections.Generic.IReadOnlyList<VocabularyEntry> learned = ProjectVocabulary.Learned(VocabularyPath);
            VocabularyEntry entry = Xunit.Assert.Single(learned);
            Xunit.Assert.Equal("quat", entry.Term);
            Xunit.Assert.Equal("FAN", entry.Label);
            Xunit.Assert.Equal(3, entry.Count);

            string block = ProjectVocabulary.Block(VocabularyPath);
            Xunit.Assert.Contains("\"quat\" means class FAN", block);
            Xunit.Assert.True(block.Length <= ProjectVocabulary.MaxBlockChars);
            Xunit.Assert.False(System.IO.File.Exists(VocabularyPath + ".tmp"));
        }

        [Xunit.Fact]
        public void WordThatIsTheClassNameIsNotStored()
        {
            ProjectVocabulary.Learn(VocabularyPath, "fan", new[] { "FAN" }, Now);
            Xunit.Assert.False(System.IO.File.Exists(VocabularyPath));
        }

        [Xunit.Fact]
        public void WordWithTwoCompetingClassesIsNotUsed()
        {
            for (int i = 0; i < 4; i++) ProjectVocabulary.Learn(VocabularyPath, "quat", new[] { "FAN" }, Now);
            for (int i = 0; i < 3; i++) ProjectVocabulary.Learn(VocabularyPath, "quat", new[] { "PUMP" }, Now);
            Xunit.Assert.Empty(ProjectVocabulary.Learned(VocabularyPath));

            for (int i = 0; i < 4; i++) ProjectVocabulary.Learn(VocabularyPath, "quat", new[] { "FAN" }, Now);
            Xunit.Assert.Equal("FAN", Xunit.Assert.Single(ProjectVocabulary.Learned(VocabularyPath)).Label);
        }

        [Xunit.Fact]
        public void FileKeepsAtMostTwoHundredPairs()
        {
            for (int prompt = 0; prompt < 12; prompt++)
            {
                System.Collections.Generic.List<string> words = new System.Collections.Generic.List<string>();
                for (int i = 0; i < ProjectVocabulary.MaxPromptTerms; i++) words.Add("w" + (prompt * 100 + i).ToString("D4"));
                ProjectVocabulary.Learn(VocabularyPath, string.Join(" ", words), new[] { "FAN" }, Now.AddMinutes(prompt));
            }

            int pairs = System.Text.RegularExpressions.Regex.Matches(System.IO.File.ReadAllText(VocabularyPath), "\"term\"").Count;
            Xunit.Assert.Equal(ProjectVocabulary.MaxTerms, pairs);
        }

        [Xunit.Fact]
        public void RemoveAndClearForgetWords()
        {
            for (int i = 0; i < 3; i++) ProjectVocabulary.Learn(VocabularyPath, "quat motor", new[] { "FAN" }, Now);
            Xunit.Assert.Equal(2, ProjectVocabulary.Learned(VocabularyPath).Count);

            Xunit.Assert.True(ProjectVocabulary.Remove(VocabularyPath, "QUAT"));
            Xunit.Assert.False(ProjectVocabulary.Remove(VocabularyPath, "quat"));
            Xunit.Assert.Equal("motor", Xunit.Assert.Single(ProjectVocabulary.Learned(VocabularyPath)).Term);

            ProjectVocabulary.Clear(VocabularyPath);
            Xunit.Assert.False(System.IO.File.Exists(VocabularyPath));
            ProjectVocabulary.Clear(VocabularyPath);
            ProjectVocabulary.Clear(null);
            Xunit.Assert.Equal(string.Empty, ProjectVocabulary.Block(VocabularyPath));
        }

        [Xunit.Fact]
        public void CorruptFileReadsAsEmptyAndIsReplacedOnLearn()
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(VocabularyPath)!);
            System.IO.File.WriteAllText(VocabularyPath, "{ not json");
            Xunit.Assert.Empty(ProjectVocabulary.Learned(VocabularyPath));

            ProjectVocabulary.Learn(VocabularyPath, "quat", new[] { "FAN" }, Now);
            Xunit.Assert.Contains("\"quat\"", System.IO.File.ReadAllText(VocabularyPath));
        }

        [Xunit.Fact]
        public void BlockIsCappedSortedAndIgnoresCounts()
        {
            System.Collections.Generic.List<VocabularyEntry> many = new System.Collections.Generic.List<VocabularyEntry>();
            for (int i = 0; i < 100; i++) many.Add(new VocabularyEntry("word" + i.ToString("D3"), "CLASS" + i, 5 + i % 7, Now));

            string block = ProjectVocabulary.Block(many);
            Xunit.Assert.True(block.Length <= ProjectVocabulary.MaxBlockChars);
            Xunit.Assert.Equal(block, ProjectVocabulary.Block(many));

            VocabularyEntry a = new VocabularyEntry("alpha", "A", 3, Now);
            VocabularyEntry b = new VocabularyEntry("beta", "B", 9, Now);
            string small = ProjectVocabulary.Block(new[] { b, a });
            Xunit.Assert.True(small.IndexOf("alpha") < small.IndexOf("beta"));
            Xunit.Assert.Equal(small, ProjectVocabulary.Block(new[] { a with { Count = 7 }, b }));
            Xunit.Assert.Equal(string.Empty, ProjectVocabulary.Block(new VocabularyEntry[0]));
        }
    }
}
