using System.Text.Json;

namespace SmartX.Core.Deployment;

/// <summary>
/// Recursively validates a multi-tier JSON configuration profile uploaded for a sensor, e.g.
/// <code>
/// { "device": { "sampleRateMs": 1000,
///               "thresholds": { "min": 15, "max": 90 },
///               "subProfiles": [ { "name": "night", "sampleRateMs": 5000 } ] } }
/// </code>
/// Every object and array is visited recursively; known keys are range-checked wherever they appear.
/// Base cases: primitive values (validated and returned) and the depth guard.
/// </summary>
public static class ConfigProfileValidator
{
    public const int MaxDepth = 12;

    private static readonly Dictionary<string, (double Min, double Max)> NumericRules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sampleRateMs"] = (100, 3_600_000),
        ["reportIntervalMs"] = (100, 3_600_000),
        ["txPowerDbm"] = (-12, 20),          // ESP32 Wi-Fi/ESP-NOW transmit power range
        ["wifiChannel"] = (1, 13),
        ["deepSleepSec"] = (0, 86_400),
        ["batteryCutoffV"] = (2.5, 4.2),
    };

    public sealed record Result(bool IsValid, int ElementsVisited, int MaxDepthReached, IReadOnlyList<string> Issues);

    public static Result Validate(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Skip });
            var issues = new List<string>();
            int visited = 0, maxDepth = 0;
            Walk(doc.RootElement, "$", 0, issues, ref visited, ref maxDepth);
            return new Result(issues.Count == 0, visited, maxDepth, issues);
        }
        catch (JsonException ex)
        {
            return new Result(false, 0, 0, [$"Invalid JSON: {ex.Message}"]);
        }
    }

    private static void Walk(JsonElement el, string path, int depth, List<string> issues, ref int visited, ref int maxDepth)
    {
        visited++;
        maxDepth = Math.Max(maxDepth, depth);

        if (depth > MaxDepth) // base case: guard
        {
            issues.Add($"{path}: nesting deeper than {MaxDepth} levels.");
            return;
        }

        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    string childPath = $"{path}.{prop.Name}";
                    CheckRule(prop.Name, prop.Value, childPath, issues);
                    if (prop.NameEquals("thresholds") && prop.Value.ValueKind == JsonValueKind.Object)
                        CheckThresholds(prop.Value, childPath, issues);
                    Walk(prop.Value, childPath, depth + 1, issues, ref visited, ref maxDepth); // recursive case
                }
                break;

            case JsonValueKind.Array:
                int i = 0;
                foreach (var item in el.EnumerateArray())
                    Walk(item, $"{path}[{i++}]", depth + 1, issues, ref visited, ref maxDepth); // recursive case
                break;

            default:
                return; // base case: primitive
        }
    }

    private static void CheckRule(string key, JsonElement value, string path, List<string> issues)
    {
        if (!NumericRules.TryGetValue(key, out var rule)) return;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var d))
        {
            issues.Add($"{path}: expected a number.");
            return;
        }
        if (d < rule.Min || d > rule.Max)
            issues.Add($"{path}: {d} is outside the safe range {rule.Min}–{rule.Max}.");
    }

    private static void CheckThresholds(JsonElement t, string path, List<string> issues)
    {
        if (t.TryGetProperty("min", out var min) && t.TryGetProperty("max", out var max)
            && min.TryGetDouble(out var a) && max.TryGetDouble(out var b) && a >= b)
        {
            issues.Add($"{path}: min ({a}) must be lower than max ({b}).");
        }
    }
}
