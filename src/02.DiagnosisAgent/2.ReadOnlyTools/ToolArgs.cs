using System.Text.Json;

namespace Portfolio.DiagnosisAgent;

/// <summary>모델이 보낸 도구 입력(JSON)을 읽는다. 형식이 틀리면 고칠 수 있게 무엇이 필요한지 알려 준다.</summary>
public static class ToolArgs
{
    public static string Required(IReadOnlyDictionary<string, JsonElement> input, string key)
        => OptString(input, key) is { Length: > 0 } s ? s : throw new DiagnosisToolException($"{key} 값이 필요합니다.");

    public static string? OptString(IReadOnlyDictionary<string, JsonElement> input, string key)
        => input.TryGetValue(key, out var v) ? Str(v) : null;

    public static int? OptInt(IReadOnlyDictionary<string, JsonElement> input, string key)
        => input.TryGetValue(key, out var v) ? Int(v) : null;

    public static bool OptBool(IReadOnlyDictionary<string, JsonElement> input, string key, bool fallback)
        => input.TryGetValue(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : fallback;

    public static IReadOnlyList<string> StringList(IReadOnlyDictionary<string, JsonElement> input, string key)
        => Array(input, key).Select(Str).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();

    public static IEnumerable<JsonElement> Array(IReadOnlyDictionary<string, JsonElement> input, string key)
        => input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    public static string? Prop(JsonElement obj, string key)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? Str(v) : null;

    public static int? PropInt(JsonElement obj, string key)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? Int(v) : null;

    private static string? Str(JsonElement v) => v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;

    private static int? Int(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetInt32(out var i)               => i,
        JsonValueKind.String when int.TryParse(v.GetString(), out var i) => i,
        _                                                                => null,
    };
}
