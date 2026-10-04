using System.Text.Json;
using ComplianceMonitor.Api.Classification;

namespace ComplianceMonitor.LabelLab;

/// <param name="Brief">True for the four cases from docs/exercise.md; accuracy is reported separately for them.</param>
public sealed record LabCase(string Action, string Guideline, ComplianceResult Expected, bool Brief = false)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<LabCase> Parse(string json)
    {
        var cases = JsonSerializer.Deserialize<List<LabCase>>(json, JsonOptions)
            ?? throw new InvalidDataException("cases.json is empty.");

        for (var i = 0; i < cases.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(cases[i].Action) || string.IsNullOrWhiteSpace(cases[i].Guideline))
            {
                throw new InvalidDataException($"cases.json item {i + 1} needs a non-empty action and guideline.");
            }
        }

        return cases;
    }
}
