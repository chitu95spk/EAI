using System.Text.Json;
using EasyLabel.Commands.Schema;

namespace EasyLabel.Commands.Tests.Schema;

public sealed class CommandArgsSchemaTests
{
    private static CommandArgsSchema Schema() => CommandArgsSchema.Parse("""
        {
          "type":"object", "additionalProperties":false,
          "properties":{
            "name":{"type":"string","pattern":"^[A-Z]+$","maxLength":4},
            "count":{"type":"integer","minimum":1,"maximum":10},
            "scope":{"enum":["current","all"],"default":"current"},
            "enabled":{"type":"boolean"}
          },
          "required":["name","count"]
        }
        """);

    private static JsonElement Args(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void ValidatesAndSuppliesDefaultsWithoutMutatingInput()
    {
        JsonElement input = Args("""{"name":"AB","count":3,"enabled":true}""");
        Assert.True(Schema().TryValidate(input, out JsonElement normalized, out string? error), error);
        Assert.Equal("current", normalized.GetProperty("scope").GetString());
        Assert.False(input.TryGetProperty("scope", out _));
    }

    [Theory]
    [InlineData("""{"count":3}""", "required")]
    [InlineData("""{"name":3,"count":3}""", "expected string")]
    [InlineData("""{"name":"abc","count":3}""", "pattern")]
    [InlineData("""{"name":"ABCDE","count":3}""", "maxLength")]
    [InlineData("""{"name":"AB","count":0}""", "minimum")]
    [InlineData("""{"name":"AB","count":11}""", "maximum")]
    [InlineData("""{"name":"AB","count":1.5}""", "integer")]
    [InlineData("""{"name":"AB","count":"3"}""", "integer")]
    [InlineData("""{"name":"AB","count":3,"scope":"remaining"}""", "enum")]
    [InlineData("""{"name":"AB","count":3,"enabled":"yes"}""", "boolean")]
    [InlineData("""{"name":"AB","count":3,"extra":1}""", "unknown")]
    [InlineData("""{"name":"AB","count":3,"count":4}""", "duplicate")]
    [InlineData("""{"name":null,"count":3}""", "string")]
    [InlineData("[]", "object")]
    [InlineData("null", "object")]
    public void RejectsInvalidArguments(string json, string expectedError)
    {
        Assert.False(Schema().TryValidate(Args(json), out _, out string? error));
        Assert.Contains(expectedError, error!);
    }

    [Fact]
    public void ValidatesNestedObjectsAndArrayItems()
    {
        var schema = CommandArgsSchema.Parse("""
            {"type":"object","properties":{"config":{"type":"object","properties":{
              "scores":{"type":"array","items":{"type":"number","minimum":0,"maximum":1}}
            }}}}
            """);
        Assert.True(schema.TryValidate(Args("""{"config":{"scores":[0,0.5,1]}}"""), out _, out _));
        Assert.False(schema.TryValidate(Args("""{"config":{"scores":[2]}}"""), out _, out _));
        Assert.False(schema.TryValidate(Args("""{"config":{"other":1}}"""), out _, out _));
    }

    [Theory]
    [InlineData("""{"type":"object","unsupported":true}""")]
    [InlineData("""{"type":"object","properties":{"x":{"type":"unknown"}}}""")]
    [InlineData("""{"type":"object","properties":{"x":{"type":"integer","default":"bad"}}}""")]
    [InlineData("""{"type":"object","properties":{"x":{"minimum":10,"maximum":1}}}""")]
    [InlineData("""{"type":"object","properties":{"x":{"pattern":"["}}}""")]
    [InlineData("""{"type":"string"}""")]
    public void RejectsInvalidSchemaAtDeclarationTime(string json) =>
        Assert.ThrowsAny<ArgumentException>(() => CommandArgsSchema.Parse(json));

    [Fact]
    public void StringLengthCountsUnicodeScalars()
    {
        var schema = CommandArgsSchema.Parse("""
            {"type":"object","properties":{"text":{"type":"string","maxLength":1}}}
            """);
        Assert.True(schema.TryValidate(Args("""{"text":"😀"}"""), out _, out _));
        Assert.False(schema.TryValidate(Args("""{"text":"😀😀"}"""), out _, out _));
    }
}
