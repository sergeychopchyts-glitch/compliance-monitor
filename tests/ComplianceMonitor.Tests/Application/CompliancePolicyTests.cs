using ComplianceMonitor.Application.Compliance;
using ComplianceMonitor.Application.Compliance.Models;

namespace ComplianceMonitor.Tests.Application;

public sealed class CompliancePolicyTests
{
    private const string Weekly = "Servers must be rebooted weekly and logs reviewed after restart";

    private static ModelEvaluation Model(ComplianceResult top, double score) => new(
        "test-provider", "test-model", "test-strategy",
        [new ModelScore("top", top, score), new ModelScore("other", top == ComplianceResult.Deviates ? ComplianceResult.Complies : ComplianceResult.Deviates, score / 2)]);

    [Fact]
    public void DecideWithoutModel_NoGuideline_IsUnclearByRuleWithNoConfidence() =>
        Assert.Equal(
            new ComplianceDecision(ComplianceResult.Unclear, null, DecisionSource.Rule, DecisionReason.NoApplicableGuideline),
            CompliancePolicy.DecideWithoutModel("No guidelines exist for this case."));

    [Fact]
    public void DecideWithoutModel_RealGuideline_LeavesItToTheModel() =>
        Assert.Null(CompliancePolicy.DecideWithoutModel(Weekly));

    [Theory]
    [InlineData(ComplianceResult.Complies, 0.9)]
    [InlineData(ComplianceResult.Deviates, 0.8)]
    [InlineData(ComplianceResult.Unclear, 0.7)]
    public void Decide_ConfidentModel_IsAcceptedWithItsScore(ComplianceResult result, double score) =>
        Assert.Equal(
            new ComplianceDecision(result, score, DecisionSource.Model, DecisionReason.ModelClassification),
            CompliancePolicy.Decide("Closed the ticket", "Closed tickets need an email", Model(result, score), 0.5));

    [Theory]
    [InlineData(0.4999, true)]
    [InlineData(0.5, false)]
    public void Decide_BelowThreshold_IsUnclearWithNoConfidence(double score, bool unclear)
    {
        var decision = CompliancePolicy.Decide("a", "b", Model(ComplianceResult.Complies, score), 0.5);

        Assert.Equal(unclear ? ComplianceResult.Unclear : ComplianceResult.Complies, decision.Result);
        Assert.Equal(unclear ? null : score, decision.Confidence);
        Assert.Equal(unclear ? DecisionReason.InsufficientModelConfidence : DecisionReason.ModelClassification, decision.Reason);
    }

    [Fact]
    public void Decide_BriefCase3_ModelSaysCompliesButNoWeeklyEvidence_Deviates() =>
        Assert.Equal(
            new ComplianceDecision(ComplianceResult.Deviates, null, DecisionSource.Rule, DecisionReason.MissingTemporalEvidence),
            CompliancePolicy.Decide("Rebooted the server and checked logs", Weekly, Model(ComplianceResult.Complies, 0.88), 0.5));

    [Fact]
    public void Decide_FrequencyStated_ModelComplianceStands()
    {
        var decision = CompliancePolicy.Decide(
            "Performed the weekly server reboot and reviewed the logs afterwards", Weekly, Model(ComplianceResult.Complies, 0.97), 0.5);

        Assert.Equal(ComplianceResult.Complies, decision.Result);
        Assert.Equal(DecisionSource.Model, decision.Source);
    }

    [Theory]
    [InlineData(ComplianceResult.Unclear)]   // e.g. an unrelated action: stays UNCLEAR, not DEVIATES
    [InlineData(ComplianceResult.Deviates)]
    public void Decide_TemporalRuleOnlyOverridesComplies(ComplianceResult modelResult)
    {
        var decision = CompliancePolicy.Decide("Watered the plants", Weekly, Model(modelResult, 0.8), 0.5);

        Assert.Equal(modelResult, decision.Result);
        Assert.Equal(DecisionSource.Model, decision.Source);
    }
}
