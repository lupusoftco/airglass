using System.Text.Json.Nodes;

namespace AirGlass.Support;

internal static class Json
{
    /// <summary>The value if <paramref name="node"/> is a JSON string.</summary>
    public static string? String(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>The value if <paramref name="node"/> is a JSON number that fits an int.</summary>
    public static int? Int(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out int number) ? number : null;
}
