using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using EasyLabel.Online;
using Xunit;

namespace EasyLabel.Online.Tests
{
    public sealed class SchemaAndBoundaryTests
    {
        [Fact]
        public void AssemblyAndProjectHaveOnlyBclDependenciesAndNoWpfOrClients()
        {
            Assembly assembly = typeof(Recipe).Assembly;
            string[] forbidden = { "EasyLabel.Commands", "EasyAI.Wpf", "AgentRunner", "PilotService", "EasyLabel.Ai.Mcp", "OpenAI", "Anthropic", "SemanticKernel", "LangChain", "System.Net.Http", "System.Net.WebClient", "System.Net.Requests", "PresentationFramework", "PresentationCore", "WindowsBase" };
            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
            {
                Assert.DoesNotContain(forbidden, f => reference.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
                Assert.True(reference.Name.StartsWith("System.", StringComparison.Ordinal) || reference.Name == "System" || reference.Name == "netstandard" || reference.Name == "mscorlib" || reference.Name == "Microsoft.CSharp", reference.FullName);
            }
            foreach (Type type in assembly.GetTypes())
                Assert.DoesNotContain(forbidden, f => type.FullName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);

            string path = Path.GetFullPath(Path.Combine(Fixtures.TestProject(), "..", "..", "src", "EasyLabel.Online", "EasyLabel.Online.csproj"));
            XDocument project = XDocument.Load(path);
            Assert.Equal("net9.0", project.Descendants("TargetFramework").Single().Value);
            Assert.Equal("false", project.Descendants("UseWPF").Single().Value);
            Assert.Empty(project.Descendants().Where(e => new[] { "ProjectReference", "PackageReference", "Reference", "FrameworkReference", "Import", "COMReference" }.Contains(e.Name.LocalName)));
            Assert.Empty(project.Descendants("Compile"));
            Assert.DoesNotContain(project.Descendants("UseWindowsForms"), e => e.Value.Equals("true", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("7.3", project.Descendants("LangVersion").Single().Value);
        }

        [Fact]
        public void SchemaArtifactDeclaresEveryObjectClosed()
        {
            using (JsonDocument schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "recipe.schema.json"))))
            {
                Assert.Equal(1, schema.RootElement.GetProperty("properties").GetProperty("schemaRev").GetProperty("const").GetInt32());
                AssertClosedObjects(schema.RootElement);
            }
        }
        private static void AssertClosedObjects(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                JsonElement kind;
                if (element.TryGetProperty("type", out kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "object")
                {
                    JsonElement additional = element.GetProperty("additionalProperties");
                    // Only explicitly typed maps can have dynamic keys.
                    Assert.True(additional.ValueKind == JsonValueKind.False || (additional.ValueKind == JsonValueKind.Object && element.TryGetProperty("propertyNames", out kind)));
                }
                foreach (JsonProperty property in element.EnumerateObject()) AssertClosedObjects(property.Value);
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in element.EnumerateArray()) AssertClosedObjects(item);
        }

        [Theory]
        [InlineData("schemaRev", "2")]
        [InlineData("nms", "1.01")]
        [InlineData("catalogVersion", "\" \"")]
        [InlineData("classes.0.minScore", "-0.1")]
        [InlineData("classes.0.minAreaPx", "-1")]
        [InlineData("classes.0.maxCount", "-1")]
        [InlineData("classes.0.maxCount", "1.5")]
        [InlineData("classes.0.roi", "[0,0,0,10]")]
        [InlineData("classes.0.roi", "[0,0,10]")]
        [InlineData("classes.0.name", "\"bad name\"")]
        [InlineData("classes.1.name", "\"scratch\"")]
        [InlineData("position.units", "\"cm\"")]
        [InlineData("position.units", "\"mm\"")]
        [InlineData("position.mode", "\"script\"")]
        [InlineData("preprocess.inputWidth", "0")]
        [InlineData("preprocess.inputHeight", "16385")]
        [InlineData("preprocess.channels", "2")]
        [InlineData("preprocess.colorOrder", "\"GRAY\"")]
        [InlineData("preprocess.interpolation", "\"automatic\"")]
        [InlineData("preprocess.scale", "0")]
        [InlineData("preprocess.std", "[1,0,1]")]
        [InlineData("preprocess.mean", "[0]")]
        [InlineData("preprocess.seed", "-1")]
        [InlineData("wire.codec", "\"http\"")]
        [InlineData("wire.maxPayloadBytes", "0")]
        [InlineData("thresholds.score", "1.1")]
        [InlineData("classMap.0", "\"unknown\"")]
        [InlineData("model.fileName", "\"../model.onnx\"")]
        [InlineData("model.sha256", "\"abc\"")]
        [InlineData("verdict.ng_if", "\"execute(scratch)\"")]
        public void InvalidRangesAndContractsAreTypedBadSchema(string path, string jsonValue)
        {
            JsonNode root = JsonNode.Parse(Encoding.UTF8.GetString(RecipeJson.Serialize(Fixtures.Recipe())));
            string[] parts = path.Split('.');
            JsonNode parent = root;
            for (int i = 0; i < parts.Length - 1; i++) parent = Child(parent, parts[i]);
            parent[parts[parts.Length - 1]] = JsonNode.Parse(jsonValue);
            Assert.Equal(RecipeInvalidReason.BadSchema, Assert.Throws<RecipeInvalid>(() => RecipeJson.Parse(root.ToJsonString())).Reason);
        }
        private static JsonNode Child(JsonNode parent, string key)
        {
            int index;
            return parent is JsonArray && int.TryParse(key, out index) ? parent[index] : parent[key];
        }

        [Theory]
        [InlineData("")]
        [InlineData("classes.0")]
        [InlineData("position")]
        [InlineData("verdict")]
        [InlineData("preprocess")]
        [InlineData("wire")]
        [InlineData("model")]
        public void UnknownFieldsAreRejectedAtEveryObjectLevel(string path)
        {
            JsonNode root = JsonNode.Parse(Encoding.UTF8.GetString(RecipeJson.Serialize(Fixtures.Recipe())));
            JsonNode target = root;
            if (path.Length > 0) foreach (string part in path.Split('.')) target = Child(target, part);
            target["unrecognized"] = true;
            Assert.Equal(RecipeInvalidReason.BadSchema, Assert.Throws<RecipeInvalid>(() => RecipeJson.Parse(root.ToJsonString())).Reason);
        }

        [Fact]
        public void DuplicateCaseChangedAndMissingFieldsAreRejected()
        {
            string json = Encoding.UTF8.GetString(RecipeJson.Serialize(Fixtures.Recipe()));
            Assert.Throws<RecipeInvalid>(() => RecipeJson.Parse(json.Replace("\"nms\":0.5", "\"nms\":0.5,\"nms\":0.6")));
            Assert.Throws<RecipeInvalid>(() => RecipeJson.Parse(json.Replace("\"schemaRev\"", "\"SchemaRev\"")));
            JsonObject root = JsonNode.Parse(json).AsObject(); root.Remove("nms");
            Assert.Throws<RecipeInvalid>(() => RecipeJson.Parse(root.ToJsonString()));
            Assert.Throws<RecipeInvalid>(() => RecipeJson.Parse("null"));
            Assert.Throws<RecipeInvalid>(() => RecipeJson.Parse(json.Replace("\"minScore\":0.45", "\"minScore\":1e999")));
            Assert.Throws<RecipeInvalid>(() => RecipeJson.Parse(json.Replace("\"0\":\"scratch\"", "\"00\":\"scratch\"")));
        }
    }
}
