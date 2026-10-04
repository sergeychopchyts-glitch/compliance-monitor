using System.Text.Json.Serialization;

namespace ComplianceMonitor.Api.Classification;

[JsonConverter(typeof(JsonStringEnumConverter<ComplianceResult>))]
public enum ComplianceResult
{
    [JsonStringEnumMemberName("COMPLIES")]
    Complies,

    [JsonStringEnumMemberName("DEVIATES")]
    Deviates,

    [JsonStringEnumMemberName("UNCLEAR")]
    Unclear,
}

/// <summary>What produced a classification outcome.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DecisionSource>))]
public enum DecisionSource
{
    /// <summary>The top zero-shot label, at or above the confidence floor.</summary>
    [JsonStringEnumMemberName("MODEL")]
    Model,

    /// <summary>A precondition rule decided without calling the model.</summary>
    [JsonStringEnumMemberName("RULE")]
    Rule,

    /// <summary>The top zero-shot score was below the confidence floor.</summary>
    [JsonStringEnumMemberName("LOW_CONFIDENCE")]
    LowConfidence,
}
