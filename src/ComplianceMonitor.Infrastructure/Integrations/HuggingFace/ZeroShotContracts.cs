using System.Text.Json.Serialization;

namespace ComplianceMonitor.Infrastructure.Integrations.HuggingFace;

// The Hugging Face zero-shot wire format (docs/hf-sample-response.json for the response).
public sealed record LabelScore(string Label, double Score);

public sealed record ZeroShotRequest(
    [property: JsonPropertyName("inputs")] string Inputs,
    [property: JsonPropertyName("parameters")] ZeroShotParameters Parameters);

public sealed record ZeroShotParameters(
    [property: JsonPropertyName("candidate_labels")] IReadOnlyList<string> CandidateLabels,
    [property: JsonPropertyName("hypothesis_template")] string HypothesisTemplate,
    [property: JsonPropertyName("multi_label")] bool MultiLabel);
