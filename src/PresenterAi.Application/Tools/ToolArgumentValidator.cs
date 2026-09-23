using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PresenterAi.Application.Tools;

public sealed record ToolValidationResult(bool IsValid, string? ErrorMessage = null)
{
    public static ToolValidationResult Valid { get; } = new(true);

    public static ToolValidationResult Invalid(string message) => new(false, message);
}

public static class ToolArgumentValidator
{
    public static ToolValidationResult Validate(JsonElement arguments, JsonObject schema)
    {
        // 1. Root type check
        var rootType = schema["type"]?.GetValue<string>();
        if (rootType == "object" && arguments.ValueKind != JsonValueKind.Object)
        {
            return ToolValidationResult.Invalid($"Expected arguments to be an object, but got '{arguments.ValueKind}'.");
        }

        var propertiesNode = schema["properties"] as JsonObject;
        var requiredNode = schema["required"] as JsonArray;
        var additionalPropertiesNode = schema["additionalProperties"];
        var disallowAdditional = additionalPropertiesNode is not null &&
                                 additionalPropertiesNode.GetValueKind() == JsonValueKind.False;

        // 2. Required properties check
        if (requiredNode is not null)
        {
            foreach (var reqItem in requiredNode)
            {
                var reqName = reqItem?.GetValue<string>();
                if (string.IsNullOrEmpty(reqName))
                {
                    continue;
                }

                if (!arguments.TryGetProperty(reqName, out var propValue) ||
                    propValue.ValueKind == JsonValueKind.Undefined ||
                    propValue.ValueKind == JsonValueKind.Null)
                {
                    return ToolValidationResult.Invalid($"Missing required property '{reqName}'.");
                }
            }
        }

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return ToolValidationResult.Valid;
        }

        // 3. Additional properties and property-level validation
        foreach (var property in arguments.EnumerateObject())
        {
            var propName = property.Name;
            var propValue = property.Value;

            JsonObject? propSchema = null;
            if (propertiesNode is not null && propertiesNode.TryGetPropertyValue(propName, out var pNode) && pNode is JsonObject pObj)
            {
                propSchema = pObj;
            }

            if (propSchema is null)
            {
                if (disallowAdditional)
                {
                    return ToolValidationResult.Invalid($"Unknown property '{propName}' is not allowed.");
                }

                continue;
            }

            // Validate property type
            var expectedType = propSchema["type"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(expectedType))
            {
                var typeCheck = CheckType(propName, propValue, expectedType);
                if (!typeCheck.IsValid)
                {
                    return typeCheck;
                }
            }

            // Validate enum
            if (propSchema["enum"] is JsonArray enumArray)
            {
                var matched = false;
                foreach (var enumItem in enumArray)
                {
                    if (MatchesEnum(propValue, enumItem))
                    {
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                {
                    return ToolValidationResult.Invalid($"Property '{propName}' has value '{propValue.GetRawText()}' which is not in the allowed enum values.");
                }
            }

            // Validate minimum / maximum for numbers
            if (propValue.ValueKind == JsonValueKind.Number)
            {
                if (propSchema.TryGetPropertyValue("minimum", out var minNode) && minNode is not null)
                {
                    var min = Convert.ToDouble(minNode.GetValue<object>(), CultureInfo.InvariantCulture);
                    if (propValue.GetDouble() < min)
                    {
                        return ToolValidationResult.Invalid($"Property '{propName}' value {propValue.GetDouble()} is less than minimum {min}.");
                    }
                }

                if (propSchema.TryGetPropertyValue("maximum", out var maxNode) && maxNode is not null)
                {
                    var max = Convert.ToDouble(maxNode.GetValue<object>(), CultureInfo.InvariantCulture);
                    if (propValue.GetDouble() > max)
                    {
                        return ToolValidationResult.Invalid($"Property '{propName}' value {propValue.GetDouble()} is greater than maximum {max}.");
                    }
                }
            }
        }

        return ToolValidationResult.Valid;
    }

    private static ToolValidationResult CheckType(string propName, JsonElement value, string expectedType)
    {
        switch (expectedType)
        {
            case "string":
                if (value.ValueKind != JsonValueKind.String)
                {
                    return ToolValidationResult.Invalid($"Property '{propName}' expected type 'string', but got '{value.ValueKind}'.");
                }
                break;
            case "integer":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out _))
                {
                    return ToolValidationResult.Invalid($"Property '{propName}' expected type 'integer', but got '{value.ValueKind}'.");
                }
                break;
            case "number":
                if (value.ValueKind != JsonValueKind.Number)
                {
                    return ToolValidationResult.Invalid($"Property '{propName}' expected type 'number', but got '{value.ValueKind}'.");
                }
                break;
            case "boolean":
                if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
                {
                    return ToolValidationResult.Invalid($"Property '{propName}' expected type 'boolean', but got '{value.ValueKind}'.");
                }
                break;
            case "object":
                if (value.ValueKind != JsonValueKind.Object)
                {
                    return ToolValidationResult.Invalid($"Property '{propName}' expected type 'object', but got '{value.ValueKind}'.");
                }
                break;
            case "array":
                if (value.ValueKind != JsonValueKind.Array)
                {
                    return ToolValidationResult.Invalid($"Property '{propName}' expected type 'array', but got '{value.ValueKind}'.");
                }
                break;
        }

        return ToolValidationResult.Valid;
    }

    private static bool MatchesEnum(JsonElement value, JsonNode? enumItem)
    {
        if (enumItem is null)
        {
            return value.ValueKind == JsonValueKind.Null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() == enumItem.GetValue<string>(),
            JsonValueKind.Number => value.TryGetInt64(out var i) && enumItem.GetValue<long>() == i ||
                                    Math.Abs(value.GetDouble() - Convert.ToDouble(enumItem.GetValue<object>(), CultureInfo.InvariantCulture)) < 1e-9,
            JsonValueKind.True => enumItem.GetValue<bool>() == true,
            JsonValueKind.False => enumItem.GetValue<bool>() == false,
            _ => value.GetRawText() == enumItem.ToJsonString()
        };
    }
}
