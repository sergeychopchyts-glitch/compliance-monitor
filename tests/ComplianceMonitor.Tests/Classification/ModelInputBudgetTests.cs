using System.Net;
using ComplianceMonitor.Api.Classification;
using ComplianceMonitor.Api.Classification.Strategies;
using ComplianceMonitor.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ComplianceMonitor.Tests.Classification;

public sealed class ModelInputBudgetTests
{
    // Placeholder prompt: "Action: " (8 bytes) + action + "\nGuideline: " (12) + guideline, and the longest
    // hypothesis "This action complies with the guideline." (40), plus 4 special tokens: 64 + action + guideline.
    private const int Overhead = 64;

    private static ModelInputCheck Check(string action, string guideline) =>
        ModelInputBudget.Check(new PlaceholderLabelStrategy().Build(action, guideline));

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
    [InlineData(900, 60, true)]   // exactly 1024
    [InlineData(900, 61, false)]  // 1025
    [InlineData(2000, 1, false)]
    public void Check_FitsUpToExactly1024(int actionLength, int guidelineLength, bool fits) =>
        Assert.Equal(fits, Check(new string('a', actionLength), new string('g', guidelineLength)).Fits);

    [Fact]
    public void Check_CountsUtf8BytesNotCharacters()
    {
        // "é" is 2 bytes in UTF-8, so 480 of them use 960 bytes: the whole budget.
        Assert.True(Check(new string('é', 480), "").Fits);
        Assert.False(Check(new string('é', 481), "").Fits);
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

    [Fact]
    public async Task Classifier_OverBudget_RefusesWithoutCallingHf()
    {
        var hf = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "[]");
        var options = Options.Create(new HuggingFaceOptions { ApiToken = "token" });
        var classifier = new ComplianceClassifier(
            TestClients.HuggingFace(hf, options.Value), new PlaceholderLabelStrategy(), options, NullLogger<ComplianceClassifier>.Instance);

        Assert.False(classifier.CheckInput(new string('a', 2000), "g").Fits);
        await Assert.ThrowsAsync<ArgumentException>(
            () => classifier.ClassifyAsync(new string('a', 2000), "g", TestContext.Current.CancellationToken));
        Assert.Empty(hf.Requests);
    }

    [Fact]
    public void Classifier_NoGuidelineRule_AlwaysFits()
    {
        var options = Options.Create(new HuggingFaceOptions { ApiToken = "token" });
        var classifier = new ComplianceClassifier(
            TestClients.HuggingFace(FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "[]"), options.Value),
            new PlaceholderLabelStrategy(), options, NullLogger<ComplianceClassifier>.Instance);

        Assert.True(classifier.CheckInput(new string('a', 2000), "No guidelines exist for this case.").Fits);
    }
}
