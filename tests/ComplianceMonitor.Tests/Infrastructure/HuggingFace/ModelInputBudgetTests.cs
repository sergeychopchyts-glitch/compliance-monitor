using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace.Strategies;

namespace ComplianceMonitor.Tests.Infrastructure.HuggingFace;

public sealed class ModelInputBudgetTests
{
    // Production prompt: "Action: " (8 bytes) + action + ". Guideline: " (13) + guideline, the longest hypothesis
    // "This action is unrelated to the guideline." (42), plus 4 special tokens: 67 + action + guideline.
    private const int Overhead = 67;

    private static ModelInputCheck Check(string action, string guideline) =>
        ModelInputBudget.Check(new ComplianceZeroShotStrategy().Build(action, guideline));

    [Fact]
    public void Check_BriefCase_FitsWithAnUpperBoundFromBytes()
    {
        const string action = "Closed ticket #48219 and sent confirmation email";
        const string guideline = "All closed tickets must include a confirmation email";

        var check = Check(action, guideline);

        Assert.True(check.Fits);
        Assert.Equal(Overhead + action.Length + guideline.Length, check.TokenUpperBound);
        Assert.Equal(1024, check.MaxTokens);
    }

    [Theory]
    [InlineData(900, 57, true)]   // exactly 1024
    [InlineData(900, 58, false)]  // 1025
    [InlineData(2000, 1, false)]
    public void Check_FitsUpToExactly1024(int actionLength, int guidelineLength, bool fits) =>
        Assert.Equal(fits, Check(new string('a', actionLength), new string('g', guidelineLength)).Fits);

    [Fact]
    public void Check_CountsUtf8BytesNotCharacters()
    {
        // "é" is 2 bytes in UTF-8: 478 of them plus a 1-byte guideline use 957 bytes, the whole budget.
        Assert.True(Check(new string('é', 478), "g").Fits);
        Assert.False(Check(new string('é', 479), "g").Fits);
    }

    [Fact]
    public void Check_UsesTheLongestHypothesis()
    {
        var prompt = new LabelPrompt("x", "{}", MultiLabel: false, new Dictionary<string, ComplianceResult>
        {
            ["short"] = ComplianceResult.Complies,
            [new string('h', 100)] = ComplianceResult.Deviates,
        });

        Assert.Equal(1 + 100 + ModelInputBudget.SpecialTokens, ModelInputBudget.Check(prompt).TokenUpperBound);
    }
}
