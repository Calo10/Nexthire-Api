using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace nexthire_api.Services;

internal static class MetaGraphPayloadBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static JsonObject ToGraphObject<T>(T? value)
    {
        if (value == null)
            return new JsonObject();

        var node = JsonSerializer.SerializeToNode(value, SerializerOptions);
        return node as JsonObject ?? new JsonObject();
    }

    public static JsonObject Merge(params JsonObject?[] layers)
    {
        var result = new JsonObject();
        foreach (var layer in layers)
        {
            if (layer == null)
                continue;

            foreach (var property in layer)
                result[property.Key] = property.Value?.DeepClone();
        }

        return result;
    }

    public static void SetRequired(JsonObject body, string key, JsonNode? value)
    {
        if (value == null)
            throw new InvalidOperationException($"Meta Graph payload is missing required field '{key}'.");

        body[key] = value;
    }
}
