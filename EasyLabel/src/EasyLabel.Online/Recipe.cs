using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EasyLabel.Online
{
    public enum RecipeInvalidReason
    {
        BadSchema, HashMismatch, MissingModel, MissingFile, ModelMismatch, InvalidPath
    }

    public sealed class RecipeInvalid : Exception
    {
        public RecipeInvalid(RecipeInvalidReason reason, string message, Exception inner = null)
            : base("RECIPE_INVALID: " + message, inner) { Reason = reason; }
        public RecipeInvalidReason Reason { get; private set; }
    }

    public sealed class Recipe
    {
        [JsonRequired] public int SchemaRev { get; set; }
        [JsonRequired] public string CatalogVersion { get; set; }
        [JsonRequired] public List<RecipeClass> Classes { get; set; }
        [JsonRequired] public PositionParameters Position { get; set; }
        [JsonRequired] public double Nms { get; set; }
        [JsonRequired] public VerdictRule Verdict { get; set; }
        [JsonRequired] public PreprocessParameters Preprocess { get; set; }
        [JsonRequired] public WireParameters Wire { get; set; }
        [JsonRequired] public Dictionary<string, double> Thresholds { get; set; }
        [JsonRequired] public Dictionary<int, string> ClassMap { get; set; }
        [JsonRequired] public ModelArtifact Model { get; set; }
    }

    public sealed class RecipeClass
    {
        [JsonRequired] public string Name { get; set; }
        [JsonRequired] public double MinScore { get; set; }
        [JsonRequired] public double MinAreaPx { get; set; }
        [JsonRequired] public int MaxCount { get; set; }
        // Null means the entire frame. Otherwise [x, y, width, height], in pixels.
        [JsonRequired] public double[] Roi { get; set; }
    }

    public sealed class PositionParameters
    {
        [JsonRequired] public string Mode { get; set; }
        [JsonRequired] public string Units { get; set; }
        [JsonRequired] public string Calib { get; set; }
    }

    public sealed class VerdictRule
    {
        [JsonRequired, JsonPropertyName("ng_if")] public string NgIf { get; set; }
    }

    public sealed class PreprocessParameters
    {
        [JsonRequired] public int InputWidth { get; set; }
        [JsonRequired] public int InputHeight { get; set; }
        [JsonRequired] public int Channels { get; set; }
        [JsonRequired] public string ColorOrder { get; set; }
        [JsonRequired] public string Interpolation { get; set; }
        [JsonRequired] public double Scale { get; set; }
        [JsonRequired] public double[] Mean { get; set; }
        [JsonRequired] public double[] Std { get; set; }
        [JsonRequired] public bool Letterbox { get; set; }
        [JsonRequired] public int Seed { get; set; }
    }

    public sealed class WireParameters
    {
        [JsonRequired] public string Codec { get; set; }
        [JsonRequired] public bool IncludeDetections { get; set; }
        [JsonRequired] public int MaxPayloadBytes { get; set; }
    }

    public sealed class ModelArtifact
    {
        [JsonRequired] public string FileName { get; set; }
        [JsonRequired] public string Sha256 { get; set; }
    }

    public sealed class Calibration
    {
        [JsonRequired] public string Id { get; set; }
        [JsonRequired] public double MmPerPixelX { get; set; }
        [JsonRequired] public double MmPerPixelY { get; set; }
        [JsonRequired] public double OriginXmm { get; set; }
        [JsonRequired] public double OriginYmm { get; set; }
    }

    // Authoring metadata only; never included in a per-frame result.
    public sealed class RecipeProvenance
    {
        [JsonRequired] public string Prompt { get; set; }
        [JsonRequired] public string AgentModel { get; set; }
        [JsonRequired] public string AgentEffort { get; set; }
        [JsonRequired] public string AgentRunId { get; set; }
        [JsonRequired] public string DatasetHash { get; set; }
        [JsonRequired] public string Author { get; set; }
        [JsonRequired] public DateTimeOffset Date { get; set; }
    }

    public interface IModelProbe
    {
        ModelProbeResult Probe(string modelFilePath);
    }

    public sealed class ModelProbeResult
    {
        public int InputWidth { get; set; }
        public int InputHeight { get; set; }
        public int Channels { get; set; }
        public Dictionary<int, string> ClassMap { get; set; }
    }
}
