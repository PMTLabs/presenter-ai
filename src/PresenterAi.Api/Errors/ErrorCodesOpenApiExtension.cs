using Microsoft.OpenApi;
using PresenterAi.Contracts;

namespace PresenterAi.Api.Errors;

public sealed class ErrorCodesOpenApiExtension : IOpenApiExtension
{
    public void Write(IOpenApiWriter writer, OpenApiSpecVersion specVersion)
    {
        writer.WriteStartArray();
        foreach (var entry in ErrorCodes.Catalogue.Values.OrderBy(entry => entry.Code))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("code");
            writer.WriteValue(entry.Code);
            writer.WritePropertyName("title");
            writer.WriteValue(entry.Title);
            writer.WritePropertyName("status");
            writer.WriteValue(entry.Status);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
