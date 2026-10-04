using System.Text.Json;
using ComplianceMonitor.Api.Classification;

namespace ComplianceMonitor.Tests.Classification;

/// <summary>
/// Default serializer options, no HTTP: the attributes alone must give the API names, because the
/// database (EnumText) and anything outside ASP.NET Core rely on them.
/// </summary>
public sealed class EnumSerializationTests
{
    [Theory]
    [InlineData(ComplianceResult.Complies, "\"COMPLIES\"")]
    [InlineData(ComplianceResult.Deviates, "\"DEVIATES\"")]
    [InlineData(ComplianceResult.Unclear, "\"UNCLEAR\"")]
    public void ComplianceResult_SerializesAsUppercaseString(ComplianceResult result, string json) =>
        Assert.Equal(json, JsonSerializer.Serialize(result));

    [Theory]
    [InlineData(DecisionSource.Model, "\"MODEL\"")]
    [InlineData(DecisionSource.Rule, "\"RULE\"")]
    [InlineData(DecisionSource.LowConfidence, "\"LOW_CONFIDENCE\"")]
    public void DecisionSource_SerializesAsUppercaseString(DecisionSource source, string json) =>
        Assert.Equal(json, JsonSerializer.Serialize(source));
}
