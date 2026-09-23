using System.Text.Json;
using System.Text.Json.Nodes;
using PresenterAi.Application.Tools;
using Xunit;

namespace PresenterAi.Application.Tests.Tools;

public sealed class ToolArgumentValidatorTests
{
    [Fact]
    public void Validate_valid_arguments_returns_valid()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject { ["type"] = "string" },
                ["count"] = new JsonObject { ["type"] = "integer" },
                ["active"] = new JsonObject { ["type"] = "boolean" }
            },
            ["required"] = new JsonArray { "name", "count" },
            ["additionalProperties"] = false
        };

        var args = ParseJson("{\"name\":\"test\",\"count\":42,\"active\":true}");
        var result = ToolArgumentValidator.Validate(args, schema);

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Validate_missing_required_property_returns_invalid()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["required_field"] = new JsonObject { ["type"] = "string" }
            },
            ["required"] = new JsonArray { "required_field" },
            ["additionalProperties"] = false
        };

        var args = ParseJson("{}");
        var result = ToolArgumentValidator.Validate(args, schema);

        Assert.False(result.IsValid);
        Assert.Contains("Missing required property 'required_field'", result.ErrorMessage);
    }

    [Fact]
    public void Validate_extra_property_when_additionalProperties_false_returns_invalid()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["allowed"] = new JsonObject { ["type"] = "string" }
            },
            ["additionalProperties"] = false
        };

        var args = ParseJson("{\"allowed\":\"ok\",\"forbidden_extra\":123}");
        var result = ToolArgumentValidator.Validate(args, schema);

        Assert.False(result.IsValid);
        Assert.Contains("Unknown property 'forbidden_extra' is not allowed", result.ErrorMessage);
    }

    [Theory]
    [InlineData("{\"val\": 123}", "string", "string")]
    [InlineData("{\"val\": \"not-a-number\"}", "integer", "integer")]
    [InlineData("{\"val\": 12.34}", "integer", "integer")]
    [InlineData("{\"val\": \"true\"}", "boolean", "boolean")]
    [InlineData("{\"val\": [1,2,3]}", "object", "object")]
    [InlineData("{\"val\": {\"a\":1}}", "array", "array")]
    public void Validate_type_mismatch_returns_invalid(string json, string schemaType, string expectedTypeInError)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["val"] = new JsonObject { ["type"] = schemaType }
            }
        };

        var args = ParseJson(json);
        var result = ToolArgumentValidator.Validate(args, schema);

        Assert.False(result.IsValid);
        Assert.Contains($"expected type '{expectedTypeInError}'", result.ErrorMessage);
    }

    [Fact]
    public void Validate_enum_allowed_value_succeeds()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["color"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray { "red", "green", "blue" }
                }
            }
        };

        var args = ParseJson("{\"color\":\"green\"}");
        var result = ToolArgumentValidator.Validate(args, schema);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_enum_disallowed_value_returns_invalid()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["color"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray { "red", "green", "blue" }
                }
            }
        };

        var args = ParseJson("{\"color\":\"yellow\"}");
        var result = ToolArgumentValidator.Validate(args, schema);

        Assert.False(result.IsValid);
        Assert.Contains("not in the allowed enum values", result.ErrorMessage);
    }

    [Theory]
    [InlineData(5, 1, 10, true)]
    [InlineData(1, 1, 10, true)]
    [InlineData(10, 1, 10, true)]
    [InlineData(0, 1, 10, false)]
    [InlineData(11, 1, 10, false)]
    public void Validate_minimum_maximum_bounds(int value, int min, int max, bool expectedValid)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["num"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = min,
                    ["maximum"] = max
                }
            }
        };

        var args = ParseJson($"{{\"num\":{value}}}");
        var result = ToolArgumentValidator.Validate(args, schema);

        Assert.Equal(expectedValid, result.IsValid);
        if (!expectedValid)
        {
            Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        }
    }

    [Fact]
    public void Validate_root_non_object_returns_invalid()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject()
        };

        var args = ParseJson("[1, 2, 3]");
        var result = ToolArgumentValidator.Validate(args, schema);

        Assert.False(result.IsValid);
        Assert.Contains("Expected arguments to be an object", result.ErrorMessage);
    }

    [Fact]
    public void Validate_ignores_keywords_outside_supported_subset_and_never_resolves_ref()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["$schema"] = "https://example.invalid/schema",
            ["unevaluatedProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["remote"] = new JsonObject { ["$ref"] = "https://example.invalid/remote-schema" }
            }
        };

        var result = ToolArgumentValidator.Validate(ParseJson("{\"remote\":{\"anything\":true},\"unknown\":1}"), schema);

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
    }

    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
