using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EasyLabel.Online;
using Xunit;

namespace EasyLabel.Online.Tests
{
    public sealed class StoreTests
    {
        [Theory]
        [InlineData(false, ".onnx")]
        [InlineData(true, ".onnx")]
        [InlineData(true, ".segai")]
        public void FreezeLoadRoundTripAndRepeatedFreezeAreDeterministic(bool calibrated, string extension)
        {
            using (StoreFixture fixture = new StoreFixture())
            {
                string source = Path.ChangeExtension(fixture.ModelPath, extension);
                if (source != fixture.ModelPath) File.Copy(fixture.ModelPath, source);
                Recipe recipe = Fixtures.Recipe();
                Calibration calibration = calibrated ? Fixtures.Calibration() : null;
                if (calibrated) { recipe.Position.Units = "mm"; recipe.Position.Calib = calibration.Id; }
                LoadedRecipe frozen = fixture.Store.Freeze(recipe, source, Fixtures.Provenance(), calibration);
                LoadedRecipe loaded = fixture.Store.Load(frozen.Id);
                Assert.Equal(frozen.Id, fixture.Store.Freeze(recipe, source, Fixtures.Provenance(), calibration).Id);
                Assert.Equal(Path.Combine(fixture.Root, "recipes", frozen.Id), frozen.Folder);
                Assert.Equal(RecipeStore.IdLength, loaded.Id.Length);
                Assert.Equal(RecipeJson.Serialize(frozen.Recipe), RecipeJson.Serialize(loaded.Recipe));
                Assert.Equal(Fixtures.Provenance().Prompt, loaded.Provenance.Prompt);
                Assert.Equal(Fixtures.Provenance().Date, loaded.Provenance.Date);
                Assert.Equal(calibrated, loaded.Calibration != null);
                Assert.True(fixture.Probe.Calls >= 3);
                Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(loaded.Folder, "model" + extension)));
                Assert.Equal(new[] { "manifest.json", "model" + extension, "model.sha256", "provenance.json", "recipe.json" }.Concat(calibrated ? new[] { "calib.json" } : new string[0]).OrderBy(x => x), Directory.GetFiles(loaded.Folder).Select(Path.GetFileName).OrderBy(x => x));
                Recipe detached = loaded.Recipe;
                detached.Nms = 0.1;
                Assert.Equal(recipe.Nms, loaded.Recipe.Nms);
                Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.Root, "recipes"), ".freeze-*"));
            }
        }

        [Theory]
        [InlineData("model.onnx")]
        [InlineData("recipe.json")]
        [InlineData("calib.json")]
        [InlineData("provenance.json")]
        [InlineData("model.sha256")]
        [InlineData("manifest.json")]
        public void TamperedPayloadIsRefused(string file)
        {
            using (StoreFixture fixture = new StoreFixture())
            {
                Recipe recipe = Fixtures.Recipe(); recipe.Position.Calib = "calib1";
                LoadedRecipe frozen = fixture.Store.Freeze(recipe, fixture.ModelPath, Fixtures.Provenance(), Fixtures.Calibration());
                File.AppendAllText(Path.Combine(frozen.Folder, file), " ");
                RecipeInvalid error = Assert.Throws<RecipeInvalid>(() => fixture.Store.Load(frozen.Id));
                Assert.Equal(RecipeInvalidReason.HashMismatch, error.Reason);
            }
        }

        [Fact]
        public void AValidThresholdEditWithoutRefreezingIsRefused()
        {
            using (StoreFixture fixture = new StoreFixture())
            {
                LoadedRecipe frozen = fixture.Store.Freeze(Fixtures.Recipe(), fixture.ModelPath, Fixtures.Provenance());
                string path = Path.Combine(frozen.Folder, "recipe.json");
                File.WriteAllText(path, File.ReadAllText(path).Replace("\"minScore\":0.45", "\"minScore\":0.46"));
                Assert.Equal(RecipeInvalidReason.HashMismatch, Assert.Throws<RecipeInvalid>(() => fixture.Store.Load(frozen.Id)).Reason);
            }
        }

        [Fact]
        public void MissingModelIsTypedAndBadIdsCannotEscapeTheStore()
        {
            using (StoreFixture fixture = new StoreFixture())
            {
                LoadedRecipe frozen = fixture.Store.Freeze(Fixtures.Recipe(), fixture.ModelPath, Fixtures.Provenance());
                File.Delete(Path.Combine(frozen.Folder, "model.onnx"));
                Assert.Equal(RecipeInvalidReason.MissingModel, Assert.Throws<RecipeInvalid>(() => fixture.Store.Load(frozen.Id)).Reason);
                Assert.Equal(RecipeInvalidReason.InvalidPath, Assert.Throws<RecipeInvalid>(() => fixture.Store.Load("../outside")).Reason);
            }
        }

        [Theory]
        [InlineData("dimensions")]
        [InlineData("channels")]
        [InlineData("classes")]
        [InlineData("null")]
        public void ProbeMismatchRefusesLoad(string mismatch)
        {
            using (StoreFixture fixture = new StoreFixture())
            {
                LoadedRecipe frozen = fixture.Store.Freeze(Fixtures.Recipe(), fixture.ModelPath, Fixtures.Provenance());
                switch (mismatch)
                {
                    case "dimensions": fixture.Probe.Result.InputWidth++; break;
                    case "channels": fixture.Probe.Result.Channels = 1; break;
                    case "classes": fixture.Probe.Result.ClassMap[0] = "dent"; break;
                    case "null": fixture.Probe.Result = null; break;
                }
                Assert.Equal(RecipeInvalidReason.ModelMismatch, Assert.Throws<RecipeInvalid>(() => fixture.Store.Load(frozen.Id)).Reason);
            }
        }

        [Fact]
        public void InvalidFreezeNeverPublishesAPackage()
        {
            using (StoreFixture fixture = new StoreFixture())
            {
                fixture.Probe.Result.InputWidth = 1;
                Assert.Equal(RecipeInvalidReason.ModelMismatch, Assert.Throws<RecipeInvalid>(() => fixture.Store.Freeze(Fixtures.Recipe(), fixture.ModelPath, Fixtures.Provenance())).Reason);
                Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.Root, "recipes")));
            }
        }

        [Theory]
        [InlineData("schema")]
        [InlineData("classMap")]
        [InlineData("rule")]
        public void RehashedButInvalidRecipeIsStillRefused(string invalid)
        {
            using (StoreFixture fixture = new StoreFixture())
            {
                LoadedRecipe frozen = fixture.Store.Freeze(Fixtures.Recipe(), fixture.ModelPath, Fixtures.Provenance());
                string path = Path.Combine(frozen.Folder, "recipe.json");
                JsonNode node = JsonNode.Parse(File.ReadAllText(path));
                if (invalid == "schema") node["schemaRev"] = 2;
                if (invalid == "classMap") node["classMap"]["0"] = "unknown";
                if (invalid == "rule") node["verdict"]["ng_if"] = "eval(scratch)";
                File.WriteAllText(path, node.ToJsonString());
                string newId = RehashPackage(frozen.Folder);
                Assert.Equal(RecipeInvalidReason.BadSchema, Assert.Throws<RecipeInvalid>(() => fixture.Store.Load(newId)).Reason);
            }
        }

        // Deliberately rebuild a content address to isolate schema validation from corruption detection.
        private static string RehashPackage(string folder)
        {
            string manifestPath = Path.Combine(folder, "manifest.json");
            JsonNode manifest = JsonNode.Parse(File.ReadAllText(manifestPath));
            foreach (string file in Directory.GetFiles(folder).Where(f => Path.GetFileName(f) != "manifest.json"))
                manifest["files"][Path.GetFileName(file)] = Hash(File.ReadAllBytes(file));
            byte[] bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
            File.WriteAllBytes(manifestPath, bytes);
            string id = Hash(bytes).Substring(0, RecipeStore.IdLength);
            Directory.Move(folder, Path.Combine(Path.GetDirectoryName(folder), id));
            return id;
        }
        private static string Hash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create()) return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
        }

        public static IEnumerable<object[]> RecipeFieldChanges()
        {
            yield return Change("catalog", r => r.CatalogVersion = "CC-13b");
            yield return Change("class name", r => { r.Classes[0].Name = "crack"; r.ClassMap[0] = "crack"; r.Verdict.NgIf = "any(crack)"; });
            yield return Change("score", r => r.Classes[0].MinScore = 0.46);
            yield return Change("area", r => r.Classes[0].MinAreaPx = 121);
            yield return Change("count", r => r.Classes[0].MaxCount = 11);
            yield return Change("roi x", r => r.Classes[0].Roi[0] = 1);
            yield return Change("roi y", r => r.Classes[0].Roi[1] = 1);
            yield return Change("roi width", r => r.Classes[0].Roi[2] = 641);
            yield return Change("roi height", r => r.Classes[0].Roi[3] = 481);
            yield return Change("roi null", r => r.Classes[0].Roi = null);
            yield return Change("position mode", r => r.Position.Mode = "centroid");
            yield return Change("position units", r => r.Position.Units = "mm");
            yield return Change("calibration id", r => r.Position.Calib = "calib2");
            yield return Change("nms", r => r.Nms = 0.51);
            yield return Change("verdict", r => r.Verdict.NgIf = "count(dent)>3");
            yield return Change("input width", r => r.Preprocess.InputWidth = 641);
            yield return Change("input height", r => r.Preprocess.InputHeight = 481);
            yield return Change("channels", r => { r.Preprocess.Channels = 1; r.Preprocess.ColorOrder = "GRAY"; r.Preprocess.Mean = new double[] { 0 }; r.Preprocess.Std = new double[] { 1 }; });
            yield return Change("color order", r => r.Preprocess.ColorOrder = "BGR");
            yield return Change("interpolation", r => r.Preprocess.Interpolation = "nearest");
            yield return Change("scale", r => r.Preprocess.Scale = 0.5);
            yield return Change("mean", r => r.Preprocess.Mean[0] = 0.1);
            yield return Change("std", r => r.Preprocess.Std[0] = 0.9);
            yield return Change("letterbox", r => r.Preprocess.Letterbox = false);
            yield return Change("seed", r => r.Preprocess.Seed++);
            yield return Change("wire detections", r => r.Wire.IncludeDetections = false);
            yield return Change("wire payload", r => r.Wire.MaxPayloadBytes++);
            yield return Change("threshold", r => r.Thresholds["score"] = 0.46);
            yield return Change("class map", r => { r.ClassMap[0] = "dent"; r.ClassMap[1] = "scratch"; });
        }
        private static object[] Change(string name, Action<Recipe> change) { return new object[] { name, change }; }

        [Theory]
        [MemberData(nameof(RecipeFieldChanges), DisableDiscoveryEnumeration = true)]
        public void EveryVariableRecipeFieldAffectsId(string name, Action<Recipe> change)
        {
            using (StoreFixture fixture = new StoreFixture())
            {
                Recipe recipe = Fixtures.Recipe(); recipe.Position.Calib = "calib1";
                string baseline = fixture.Store.Freeze(recipe, fixture.ModelPath, Fixtures.Provenance(), Fixtures.Calibration()).Id;
                change(recipe);
                Calibration calibration = Fixtures.Calibration(); calibration.Id = recipe.Position.Calib;
                fixture.Probe.Result.InputWidth = recipe.Preprocess.InputWidth;
                fixture.Probe.Result.InputHeight = recipe.Preprocess.InputHeight;
                fixture.Probe.Result.Channels = recipe.Preprocess.Channels;
                fixture.Probe.Result.ClassMap = new Dictionary<int, string>(recipe.ClassMap);
                Assert.NotEqual(baseline, fixture.Store.Freeze(recipe, fixture.ModelPath, Fixtures.Provenance(), calibration).Id);
                Assert.False(string.IsNullOrEmpty(name));
            }
        }

        [Fact]
        public void EveryProvenanceFieldAffectsId()
        {
            Action<RecipeProvenance>[] changes = {
                p => p.Prompt += "!", p => p.AgentModel += "!", p => p.AgentEffort = "low", p => p.AgentRunId += "!",
                p => p.DatasetHash = new string('b', 64), p => p.Author += "!", p => p.Date = p.Date.AddTicks(1)
            };
            using (StoreFixture fixture = new StoreFixture())
            {
                string baseline = fixture.Store.Freeze(Fixtures.Recipe(), fixture.ModelPath, Fixtures.Provenance()).Id;
                foreach (Action<RecipeProvenance> change in changes)
                {
                    RecipeProvenance provenance = Fixtures.Provenance(); change(provenance);
                    Assert.NotEqual(baseline, fixture.Store.Freeze(Fixtures.Recipe(), fixture.ModelPath, provenance).Id);
                }
            }
        }

        [Fact]
        public void EveryCalibrationFieldAndOptionalPresenceAffectId()
        {
            Action<Calibration>[] changes = { c => c.MmPerPixelX += 0.1, c => c.MmPerPixelY += 0.1, c => c.OriginXmm++, c => c.OriginYmm++ };
            using (StoreFixture fixture = new StoreFixture())
            {
                Recipe recipe = Fixtures.Recipe(); recipe.Position.Calib = "calib1";
                string baseline = fixture.Store.Freeze(recipe, fixture.ModelPath, Fixtures.Provenance(), Fixtures.Calibration()).Id;
                foreach (Action<Calibration> change in changes)
                {
                    Calibration calibration = Fixtures.Calibration(); change(calibration);
                    Assert.NotEqual(baseline, fixture.Store.Freeze(recipe, fixture.ModelPath, Fixtures.Provenance(), calibration).Id);
                }
                recipe.Position.Calib = null;
                Assert.NotEqual(baseline, fixture.Store.Freeze(recipe, fixture.ModelPath, Fixtures.Provenance()).Id);
            }
        }

        [Fact]
        public void ModelBytesAndFormatAffectIdAndModelHashIsPersisted()
        {
            using (StoreFixture fixture = new StoreFixture())
            {
                LoadedRecipe first = fixture.Store.Freeze(Fixtures.Recipe(), fixture.ModelPath, Fixtures.Provenance());
                Assert.Equal(Hash(File.ReadAllBytes(fixture.ModelPath)), first.Recipe.Model.Sha256);
                File.AppendAllText(fixture.ModelPath, "changed");
                Assert.NotEqual(first.Id, fixture.Store.Freeze(Fixtures.Recipe(), fixture.ModelPath, Fixtures.Provenance()).Id);
                string segai = Path.ChangeExtension(fixture.ModelPath, ".segai");
                File.WriteAllBytes(segai, File.ReadAllBytes(Path.Combine(first.Folder, "model.onnx")));
                Assert.NotEqual(first.Id, fixture.Store.Freeze(Fixtures.Recipe(), segai, Fixtures.Provenance()).Id);
            }
        }

        [Fact]
        public void DictionaryInsertionOrderDoesNotAffectId()
        {
            using (StoreFixture fixture = new StoreFixture())
            {
                Recipe recipe = Fixtures.Recipe(); recipe.Thresholds["area"] = 0.1;
                string id = fixture.Store.Freeze(recipe, fixture.ModelPath, Fixtures.Provenance()).Id;
                recipe.ClassMap = new Dictionary<int, string> { { 1, "dent" }, { 0, "scratch" } };
                recipe.Thresholds = new Dictionary<string, double> { { "area", 0.1 }, { "score", 0.45 } };
                Assert.Equal(id, fixture.Store.Freeze(recipe, fixture.ModelPath, Fixtures.Provenance()).Id);
            }
        }
    }
}
