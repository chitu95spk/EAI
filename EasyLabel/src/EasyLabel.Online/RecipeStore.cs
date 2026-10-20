using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace EasyLabel.Online
{
    internal sealed class RecipeManifest
    {
        public RecipeManifest() { }
        [JsonRequired] public int SchemaRev { get; set; }
        [JsonRequired] public Dictionary<string, string> Files { get; set; }
    }

    public sealed class LoadedRecipe
    {
        private readonly byte[] recipeBytes;
        private readonly byte[] provenanceBytes;
        private readonly byte[] calibrationBytes;
        internal LoadedRecipe(string id, string folder, byte[] recipe, byte[] provenance, byte[] calibration)
        { Id = id; Folder = folder; recipeBytes = recipe; provenanceBytes = provenance; calibrationBytes = calibration; }
        public string Id { get; private set; }
        public string Folder { get; private set; }
        public Recipe Recipe { get { return RecipeJson.Parse(Encoding.UTF8.GetString(recipeBytes)); } }
        public RecipeProvenance Provenance { get { return RecipeJson.Decode<RecipeProvenance>(Encoding.UTF8.GetString(provenanceBytes)); } }
        public Calibration Calibration { get { return calibrationBytes == null ? null : RecipeJson.Decode<Calibration>(Encoding.UTF8.GetString(calibrationBytes)); } }
    }

    /// <summary>
    /// Immutable content-addressed packages. ID is the first 24 lowercase SHA256 hex characters of
    /// canonical manifest.json; the manifest hashes every payload byte, including model.sha256.
    /// Hashes detect corruption; they do not authenticate an author. No model runtime is linked here.
    /// </summary>
    public sealed class RecipeStore
    {
        public const int IdLength = 24;
        private readonly string recipesRoot;
        private readonly IModelProbe probe;

        public RecipeStore(string projectDirectory, IModelProbe modelProbe)
        {
            if (string.IsNullOrWhiteSpace(projectDirectory)) throw new ArgumentException("A project directory is required.");
            probe = modelProbe ?? throw new ArgumentNullException(nameof(modelProbe));
            recipesRoot = Path.Combine(Path.GetFullPath(projectDirectory), "recipes");
            RejectLinks(recipesRoot);
        }

        public LoadedRecipe Freeze(Recipe recipe, string modelFilePath, RecipeProvenance provenance, Calibration calibration = null)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));
            RecipeValidation.Validate(provenance);
            string source = Path.GetFullPath(modelFilePath);
            RejectLinks(source);
            if (!File.Exists(source)) throw new RecipeInvalid(RecipeInvalidReason.MissingModel, "Model file is missing.");
            string extension = Path.GetExtension(source).ToLowerInvariant();
            RecipeValidation.Require(extension == ".onnx" || extension == ".segai", "Model must be .onnx or .segai.");
            Recipe snapshot = RecipeJson.Decode<Recipe>(Encoding.UTF8.GetString(RecipeJson.Encode(recipe)));
            // Snapshot authoring metadata too: later caller edits cannot change the published bytes.
            byte[] provenanceBytes = RecipeJson.Encode(provenance);
            byte[] calibrationBytes = calibration == null ? null : RecipeJson.Encode(calibration);
            Calibration calibrationSnapshot = calibrationBytes == null ? null : RecipeJson.Decode<Calibration>(Encoding.UTF8.GetString(calibrationBytes));
            RejectLinks(recipesRoot);
            Directory.CreateDirectory(recipesRoot);
            string stage = Path.Combine(recipesRoot, ".freeze-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                string modelName = "model" + extension;
                string stagedModel = Path.Combine(stage, modelName);
                File.Copy(source, stagedModel, false);
                string modelHash = HashFile(stagedModel);
                snapshot.Model = new ModelArtifact { FileName = modelName, Sha256 = modelHash };
                byte[] recipeBytes = RecipeJson.Serialize(snapshot);
                RecipeValidation.Validate(calibrationSnapshot, snapshot);
                CheckProbe(stagedModel, snapshot);
                if (HashFile(stagedModel) != modelHash) Mismatch("Model changed during probing.");
                File.WriteAllBytes(Path.Combine(stage, "recipe.json"), recipeBytes);
                File.WriteAllBytes(Path.Combine(stage, "provenance.json"), provenanceBytes);
                File.WriteAllBytes(Path.Combine(stage, "model.sha256"), Encoding.ASCII.GetBytes(modelHash + "\n"));
                if (calibrationBytes != null) File.WriteAllBytes(Path.Combine(stage, "calib.json"), calibrationBytes);
                Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string file in Directory.GetFiles(stage)) hashes.Add(Path.GetFileName(file), HashFile(file));
                byte[] manifest = RecipeJson.Encode(new RecipeManifest { SchemaRev = 1, Files = hashes });
                File.WriteAllBytes(Path.Combine(stage, "manifest.json"), manifest);
                string id = HashBytes(manifest).Substring(0, IdLength);
                string destination = Path.Combine(recipesRoot, id);
                if (Directory.Exists(destination)) return LoadExisting(id, manifest);
                try { Directory.Move(stage, destination); }
                catch (IOException)
                {
                    if (!Directory.Exists(destination)) throw;
                    return LoadExisting(id, manifest);
                }
                return new LoadedRecipe(id, destination, recipeBytes, provenanceBytes, calibrationBytes);
            }
            finally
            {
                // Only this invocation's fixed child staging directory can be removed.
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
            }
        }

        public LoadedRecipe Load(string id)
        {
            if (id == null || id.Length != IdLength || !id.All(c => (c >= 'a' && c <= 'f') || (c >= '0' && c <= '9')))
                throw new RecipeInvalid(RecipeInvalidReason.InvalidPath, "Recipe id must be 24 lowercase hex characters.");
            string folder = Path.Combine(recipesRoot, id);
            try
            {
                RejectLinks(folder);
                byte[] manifestBytes = ReadJson(folder, "manifest.json");
                if (HashBytes(manifestBytes).Substring(0, IdLength) != id) Mismatch("Manifest does not match recipe id.");
                RecipeManifest manifest = RecipeJson.Decode<RecipeManifest>(Encoding.UTF8.GetString(manifestBytes));
                RecipeValidation.Require(manifest.SchemaRev == 1 && manifest.Files != null, "Invalid manifest schema.");
                HashSet<string> allowed = new HashSet<string>(new[] { "recipe.json", "provenance.json", "model.onnx", "model.segai", "model.sha256", "calib.json" }, StringComparer.Ordinal);
                RecipeValidation.Require(manifest.Files.Count >= 4 && manifest.Files.Count <= 5 && manifest.Files.All(p => allowed.Contains(p.Key) && RecipeValidation.Hash(p.Value))
                    && manifest.Files.ContainsKey("recipe.json") && manifest.Files.ContainsKey("provenance.json") && manifest.Files.ContainsKey("model.sha256")
                    && (manifest.Files.ContainsKey("model.onnx") ^ manifest.Files.ContainsKey("model.segai")), "Manifest must name exactly one model and the required payloads.");
                foreach (KeyValuePair<string, string> entry in manifest.Files)
                {
                    string file = Path.Combine(folder, entry.Key);
                    RejectLinks(file);
                    if (!File.Exists(file)) throw new RecipeInvalid(entry.Key == "model.onnx" || entry.Key == "model.segai" ? RecipeInvalidReason.MissingModel : RecipeInvalidReason.MissingFile, "Missing " + entry.Key);
                    if (HashFile(file) != entry.Value) Mismatch("Hash mismatch: " + entry.Key);
                }
                if (Directory.GetDirectories(folder).Length != 0 || Directory.GetFiles(folder).Length != manifest.Files.Count + 1)
                    Mismatch("Unexpected files or directories in recipe package.");
                byte[] recipeBytes = ReadVerifiedJson(folder, "recipe.json", manifest.Files["recipe.json"]);
                Recipe loaded = RecipeJson.Parse(Encoding.UTF8.GetString(recipeBytes));
                if (!manifest.Files.ContainsKey(loaded.Model.FileName)) throw new RecipeInvalid(RecipeInvalidReason.MissingModel, "Recipe model is not in the manifest.");
                if (manifest.Files[loaded.Model.FileName] != loaded.Model.Sha256
                    || !File.ReadAllBytes(Path.Combine(folder, "model.sha256")).SequenceEqual(Encoding.ASCII.GetBytes(loaded.Model.Sha256 + "\n")))
                    Mismatch("Model SHA256 disagrees with recipe or sidecar.");
                byte[] provenanceBytes = ReadVerifiedJson(folder, "provenance.json", manifest.Files["provenance.json"]);
                RecipeValidation.Validate(RecipeJson.Decode<RecipeProvenance>(Encoding.UTF8.GetString(provenanceBytes)));
                byte[] calibrationBytes = manifest.Files.ContainsKey("calib.json") ? ReadVerifiedJson(folder, "calib.json", manifest.Files["calib.json"]) : null;
                Calibration calibration = calibrationBytes == null ? null : RecipeJson.Decode<Calibration>(Encoding.UTF8.GetString(calibrationBytes));
                RecipeValidation.Validate(calibration, loaded);
                CheckProbe(Path.Combine(folder, loaded.Model.FileName), loaded);
                if (HashFile(Path.Combine(folder, loaded.Model.FileName)) != loaded.Model.Sha256) Mismatch("Model changed during probing.");
                return new LoadedRecipe(id, folder, recipeBytes, provenanceBytes, calibrationBytes);
            }
            catch (RecipeInvalid) { throw; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            { throw new RecipeInvalid(RecipeInvalidReason.MissingFile, "Cannot read recipe package: " + ex.Message, ex); }
        }

        private LoadedRecipe LoadExisting(string id, byte[] expectedManifest)
        {
            LoadedRecipe existing = Load(id);
            if (!ReadJson(existing.Folder, "manifest.json").SequenceEqual(expectedManifest)) Mismatch("Recipe id prefix collision.");
            return existing;
        }

        private void CheckProbe(string modelPath, Recipe recipe)
        {
            ModelProbeResult result;
            try { result = probe.Probe(modelPath); }
            catch (Exception ex) { throw new RecipeInvalid(RecipeInvalidReason.ModelMismatch, "Model probe failed: " + ex.Message, ex); }
            if (result == null || result.InputWidth != recipe.Preprocess.InputWidth || result.InputHeight != recipe.Preprocess.InputHeight
                || result.Channels != recipe.Preprocess.Channels || result.ClassMap == null || result.ClassMap.Count != recipe.ClassMap.Count
                || recipe.ClassMap.Any(p => !result.ClassMap.ContainsKey(p.Key) || result.ClassMap[p.Key] != p.Value))
                throw new RecipeInvalid(RecipeInvalidReason.ModelMismatch, "Model input dimensions/channels or class map disagree with recipe.");
        }

        private static byte[] ReadJson(string folder, string name)
        {
            string path = Path.Combine(folder, name);
            RejectLinks(path);
            if (!File.Exists(path)) throw new RecipeInvalid(RecipeInvalidReason.MissingFile, "Missing " + name);
            if (new FileInfo(path).Length > 2097152) throw new RecipeInvalid(RecipeInvalidReason.BadSchema, name + " exceeds 2 MiB.");
            return File.ReadAllBytes(path);
        }
        private static byte[] ReadVerifiedJson(string folder, string name, string expectedHash)
        {
            byte[] bytes = ReadJson(folder, name);
            if (HashBytes(bytes) != expectedHash) Mismatch("Payload changed while loading: " + name);
            return bytes;
        }
        private static void Mismatch(string message) { throw new RecipeInvalid(RecipeInvalidReason.HashMismatch, message); }
        private static string HashFile(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create()) return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        private static string HashBytes(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create()) return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
        }
        private static void RejectLinks(string path)
        {
            string current = Path.GetFullPath(path);
            while (current != null)
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new RecipeInvalid(RecipeInvalidReason.InvalidPath, "Recipe paths must not traverse symbolic links or junctions.");
                current = Path.GetDirectoryName(current);
            }
        }
    }
}
