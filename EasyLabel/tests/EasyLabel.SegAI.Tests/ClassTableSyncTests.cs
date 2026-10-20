using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using EasyLabel.SegAI.Model;
using Newtonsoft.Json.Linq;
using Xunit;

namespace EasyLabel.SegAI.Tests
{
    public sealed class ClassTableSyncTests
    {
        private static SegAiClassTable DefectOnly()
        {
            return new SegAiClassTable(new[] { new SegAiClassEntry("NG", SegAiClassKind.Defect, true) });
        }

        [Fact]
        public void MissingNamesAppendWithoutChangingExistingEntries()
        {
            SegAiClassTable original = new SegAiClassTable(new[] {
                new SegAiClassEntry("NG", SegAiClassKind.Defect, false),
                new SegAiClassEntry("OK", SegAiClassKind.Good, true) });
            IReadOnlyList<string> skipped;
            var result = ClassTableSyncRules.Extend(original, new[] { "NG", "OK", "LECH", "LECH_BOT" }, out skipped);
            Assert.NotNull(result);
            Assert.Equal(new[] { "NG", "OK", "LECH", "LECH_BOT" }, result.Classes.Select(c => c.Name));
            Assert.Same(original.Classes[0], result.Classes[0]);
            Assert.Same(original.Classes[1], result.Classes[1]);
            Assert.False(result.Classes[0].Enabled);
            Assert.All(result.Classes.Skip(2), c => { Assert.True(c.Enabled); Assert.Equal(SegAiClassKind.Defect, c.Kind); });
            Assert.Null(SegAiClassTable.Validate(result.Classes));
            Assert.Empty(skipped);
            Assert.Equal(2, original.Classes.Count);
        }

        [Theory]
        [InlineData("NG", "OK")]
        [InlineData(" ng ", "ok")]
        [InlineData("NG", " OK ")]
        public void ExistingCaseAndPaddingNamesDoNotAdd(string first, string second)
        {
            IReadOnlyList<string> skipped;
            Assert.Null(ClassTableSyncRules.Extend(SegAiClassTable.Legacy(), new[] { first, second }, out skipped));
            Assert.Empty(skipped);
        }

        [Fact]
        public void FirstSeenOrderAndSpellingAreStable()
        {
            IReadOnlyList<string> skipped;
            string[] labels = { " B ", "a", "b", " A ", "C" };
            var result = ClassTableSyncRules.Extend(DefectOnly(), labels, out skipped);
            Assert.NotNull(result);
            Assert.Equal(new[] { "NG", "B", "a", "C" }, result.Classes.Select(c => c.Name));
            Assert.Null(ClassTableSyncRules.Extend(result, labels, out skipped));
        }

        [Theory]
        [InlineData(" OK ", "OK")]
        [InlineData("good", "good")]
        [InlineData(" tOt ", "tOt")]
        public void GoodAliasesUseGoodKind(string input, string expected)
        {
            IReadOnlyList<string> skipped;
            var result = ClassTableSyncRules.Extend(DefectOnly(), new[] { input }, out skipped);
            Assert.NotNull(result);
            Assert.Equal(expected, result.Classes[1].Name);
            Assert.Equal(SegAiClassKind.Good, result.Classes[1].Kind);
            Assert.True(result.Classes[1].Enabled);
            Assert.Null(SegAiClassTable.Validate(result.Classes));
        }

        [Fact]
        public void ASecondGoodClassIsRejectedAndReported()
        {
            IReadOnlyList<string> skipped;
            var result = ClassTableSyncRules.Extend(SegAiClassTable.Legacy(), new[] { "GOOD", "LECH" }, out skipped);
            Assert.NotNull(result);
            Assert.Equal(new[] { "NG", "OK", "LECH" }, result.Classes.Select(c => c.Name));
            Assert.Contains("GOOD: more than one GOOD class", skipped);
            Assert.Null(SegAiClassTable.Validate(result.Classes));
        }

        [Fact]
        public void InvalidNamesAreSkippedAndReported()
        {
            IReadOnlyList<string> skipped;
            var result = ClassTableSyncRules.Extend(DefectOnly(),
                new[] { new string('x', 65), "bad\0name", "bad\u0085name", "bad\ud800name", "valid" }, out skipped);
            Assert.NotNull(result);
            Assert.Equal(new[] { "NG", "valid" }, result.Classes.Select(c => c.Name));
            Assert.Equal(4, skipped.Count);
            Assert.Null(SegAiClassTable.Validate(result.Classes));
        }

        [Fact]
        public void NameLimitCountsUtf8Bytes()
        {
            IReadOnlyList<string> skipped;
            string accepted = new string('é', 32);
            var result = ClassTableSyncRules.Extend(DefectOnly(), new[] { new string('é', 33), accepted }, out skipped);
            Assert.NotNull(result);
            Assert.Equal(accepted, result.Classes[1].Name);
            Assert.Single(skipped);
        }

        [Fact]
        public void EmptyAndBlankAnnotationsDoNothing()
        {
            IReadOnlyList<string> skipped;
            Assert.Null(ClassTableSyncRules.Extend(DefectOnly(), Array.Empty<string>(), out skipped));
            Assert.Empty(skipped);
            string[] blank = new string[3];
            blank[1] = "";
            blank[2] = " \t ";
            Assert.Null(ClassTableSyncRules.Extend(DefectOnly(), blank, out skipped));
            Assert.Empty(skipped);
        }

        [Fact]
        public void ClassLimitSkipsOnlyTheOverflow()
        {
            var entries = Enumerable.Range(1, 31).Select(i => new SegAiClassEntry("c" + i, SegAiClassKind.Defect, true)).ToList();
            IReadOnlyList<string> skipped;
            var result = ClassTableSyncRules.Extend(new SegAiClassTable(entries), new[] { "last", "overflow" }, out skipped);
            Assert.NotNull(result);
            Assert.Equal(32, result.Classes.Count);
            Assert.Equal("last", result.Classes[31].Name);
            Assert.Single(skipped);
            Assert.Null(SegAiClassTable.Validate(result.Classes));
        }

        [Fact]
        public void SessionSynchronizesAllAnnotationsAndPreservesTheirExactBytes()
        {
            string root = NewProject();
            using (SegAiSession session = SegAiSession.Create(root))
            {
                string images = Directory.CreateDirectory(Path.Combine(root, "images")).FullName;
                string first = WriteJson(images, "01", "NG", "OK", "LECH");
                string second = WriteJson(images, "02", "LECH_BOT", " lech ");
                string[] paths = { first, second };
                byte[][] before = paths.Select(File.ReadAllBytes).ToArray();
                // Neither metadata nor legacy sidecars are annotation sources.
                WriteJson(session.Paths.MetaDir, "not-annotation", "METADATA");
                File.WriteAllText(Path.Combine(images, "legacy.easylabel.json"), "{\"shapes\":[{\"label\":\"SIDECAR\"}]}");
                IReadOnlyList<string> skipped;
                Assert.Equal(new[] { "LECH", "LECH_BOT" }, session.SynchronizeClassTableFromAnnotations(out skipped));
                Assert.Empty(skipped);
                string[] expected = { "NG", "OK", "LECH", "LECH_BOT" };
                Assert.Equal(expected, session.ClassTable.Classes.Select(c => c.Name));
                Assert.Equal(expected, File.ReadAllLines(session.Paths.ClassesFile).Where(s => s.Length > 0));
                string advancedPath = Path.Combine(session.Paths.MetaDir, "advanced.json");
                JObject advanced = JObject.Parse(File.ReadAllText(advancedPath));
                JArray savedEntries = Assert.IsType<JArray>(advanced["segClasses"]);
                Assert.Equal(expected, savedEntries.Select(c => c.Value<string>("name")));
                Assert.Equal(new[] { "defect", "good", "defect", "defect" }, savedEntries.Select(c => c.Value<string>("kind")));
                Assert.All(savedEntries, c => Assert.True(c.Value<bool>("enabled")));
                byte[] savedTable = File.ReadAllBytes(advancedPath);
                byte[] savedClasses = File.ReadAllBytes(session.Paths.ClassesFile);
                Assert.Empty(session.SynchronizeClassTableFromAnnotations(out skipped));
                Assert.Equal(savedTable, File.ReadAllBytes(advancedPath));
                Assert.Equal(savedClasses, File.ReadAllBytes(session.Paths.ClassesFile));
                for (int i = 0; i < paths.Length; i++) Assert.Equal(before[i], File.ReadAllBytes(paths[i]));
            }
            using (SegAiSession reopened = SegAiSession.Create(root))
                Assert.Equal(new[] { "NG", "OK", "LECH", "LECH_BOT" }, reopened.ClassTable.Classes.Select(c => c.Name));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FailedClassesWriteRestoresMetadataAndMap(bool existingAdvanced)
        {
            string root = NewProject();
            using (SegAiSession session = SegAiSession.Create(root))
            {
                string advancedPath = Path.Combine(session.Paths.MetaDir, "advanced.json");
                if (existingAdvanced) File.WriteAllText(advancedPath, "{\"other\": {\"keep\": 42}}");
                byte[] beforeAdvanced = existingAdvanced ? File.ReadAllBytes(advancedPath) : Array.Empty<byte>();
                WriteJson(root, "01", "LECH");
                byte[] beforeClasses = File.ReadAllBytes(session.Paths.ClassesFile);
                using (var held = new FileStream(session.Paths.ClassesFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    IReadOnlyList<string> skipped;
                    Assert.ThrowsAny<IOException>(() => session.SynchronizeClassTableFromAnnotations(out skipped));
                    Assert.Equal(new[] { "NG", "OK" }, session.ClassTable.Classes.Select(c => c.Name));
                    Assert.Equal(beforeClasses, File.ReadAllBytes(session.Paths.ClassesFile));
                    Assert.Equal(existingAdvanced, File.Exists(advancedPath));
                    if (existingAdvanced) Assert.Equal(beforeAdvanced, File.ReadAllBytes(advancedPath));
                }
                IReadOnlyList<string> retrySkipped;
                Assert.Equal(new[] { "LECH" }, session.SynchronizeClassTableFromAnnotations(out retrySkipped));
            }
        }

        [Fact]
        public void InvalidJsonDoesNotSaveAPartialDiscovery()
        {
            using (SegAiSession session = SegAiSession.Create(NewProject()))
            {
                WriteJson(session.ProjectRoot, "01", "LECH");
                File.WriteAllText(Path.Combine(session.ProjectRoot, "02.json"), "{broken");
                IReadOnlyList<string> skipped;
                Assert.Throws<InvalidDataException>(() => session.SynchronizeClassTableFromAnnotations(out skipped));
                Assert.Equal(new[] { "NG", "OK" }, session.ClassTable.Classes.Select(c => c.Name));
                Assert.False(File.Exists(Path.Combine(session.Paths.MetaDir, "advanced.json")));
            }
        }

        [Fact]
        public void ExtendedTableStillRejectsTheOldNgOkModel()
        {
            IReadOnlyList<string> skipped;
            var extended = ClassTableSyncRules.Extend(SegAiClassTable.Legacy(), new[] { "LECH", "LECH_BOT" }, out skipped);
            Assert.NotNull(extended);
            Assert.Throws<InvalidOperationException>(() => SegAiSession.DecideClassPrediction(extended, SegAiClassTable.Legacy()));
            Assert.Throws<InvalidOperationException>(() => SegAiSession.DecideClassPrediction(extended, null));
        }

        [Fact]
        public void NoMissingNamesAcross250JsonsDoesNotWriteTheTable()
        {
            using (SegAiSession session = SegAiSession.Create(NewProject()))
            {
                for (int i = 0; i < 250; i++) WriteJson(session.ProjectRoot, i.ToString("D3"), "NG", "OK");
                byte[] classes = File.ReadAllBytes(session.Paths.ClassesFile);
                DateTime timestamp = File.GetLastWriteTimeUtc(session.Paths.ClassesFile);
                var timer = Stopwatch.StartNew();
                IReadOnlyList<string> skipped;
                Assert.Empty(session.SynchronizeClassTableFromAnnotations(out skipped));
                timer.Stop();
                File.WriteAllText(Path.Combine(session.ProjectRoot, "scan-timing.txt"), "jsons=250; elapsed_ms=" + timer.Elapsed.TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                Assert.Empty(skipped);
                Assert.Equal(classes, File.ReadAllBytes(session.Paths.ClassesFile));
                Assert.Equal(timestamp, File.GetLastWriteTimeUtc(session.Paths.ClassesFile));
                Assert.False(File.Exists(Path.Combine(session.Paths.MetaDir, "advanced.json")));
            }
        }

        private static string NewProject()
        {
            var parent = Environment.GetEnvironmentVariable("EAI_CLS_TEST_ROOT");
            if (string.IsNullOrWhiteSpace(parent))
            {
                // Default to <repo>/tmp/EAI-CLS/synthetic (project-local scratch), found by walking up to the folder that holds CLAUDE.md.
                string dir = AppContext.BaseDirectory;
                while (dir != null && !File.Exists(Path.Combine(dir, "CLAUDE.md"))) dir = Path.GetDirectoryName(dir);
                if (dir == null) throw new InvalidOperationException("Set EAI_CLS_TEST_ROOT to tmp/EAI-CLS/synthetic in this workspace.");
                parent = Path.Combine(dir, "tmp", "EAI-CLS", "synthetic");
                Directory.CreateDirectory(parent);
            }
            string root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static string WriteJson(string folder, string stem, params string[] labels)
        {
            var shapes = new JArray(labels.Select(label => new JObject {
                ["label"] = label, ["shape_type"] = "oriented_rectangle", ["points"] = new JArray(new JArray(1, 1), new JArray(4, 1), new JArray(4, 4), new JArray(1, 4)),
                ["flags"] = new JObject(), ["custom"] = "preserved" }));
            var document = new JObject { ["shapes"] = shapes, ["imagePath"] = stem + ".png", ["imageWidth"] = 8, ["imageHeight"] = 8, ["flags"] = new JObject() };
            string path = Path.Combine(folder, stem + ".json");
            File.WriteAllText(path, document.ToString());
            return path;
        }
    }
}
