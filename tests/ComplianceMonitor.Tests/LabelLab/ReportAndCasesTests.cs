using System.Text.Json;
using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.LabelLab;

namespace ComplianceMonitor.Tests.LabelLab;

public sealed class ReportAndCasesTests
{
    private static CaseResult Result(int number, bool brief, ComplianceResult expected, ComplianceResult? predicted, string action = "Did a thing") =>
        new(number, new LabCase(action, "Rule", expected, brief), predicted, DecisionReason.ModelClassification, predicted, 0.75, 0.5, null);

    [Fact]
    public void Parse_ReadsCasesWithBriefDefaultingToFalse()
    {
        var cases = LabCase.Parse("""
            [
              { "brief": true, "action": "a", "guideline": "g", "expected": "COMPLIES" },
              { "action": "b", "guideline": "g", "expected": "UNCLEAR" }
            ]
            """);

        Assert.Equal(
            [new LabCase("a", "g", ComplianceResult.Complies, true), new LabCase("b", "g", ComplianceResult.Unclear)],
            cases);
    }

    [Fact]
    public void Parse_BlankAction_Throws() =>
        Assert.Throws<InvalidDataException>(() => LabCase.Parse("""[{ "action": " ", "guideline": "g", "expected": "COMPLIES" }]"""));

    [Fact]
    public void Parse_UnknownExpected_Throws() =>
        Assert.Throws<JsonException>(() => LabCase.Parse("""[{ "action": "a", "guideline": "g", "expected": "MAYBE" }]"""));

    [Fact]
    public void Accuracy_IsReportedSeparatelyForBriefAndExtraCases()
    {
        var report = new StrategyReport("s",
        [
            Result(1, brief: true, ComplianceResult.Complies, ComplianceResult.Complies),
            Result(2, brief: true, ComplianceResult.Deviates, ComplianceResult.Complies),
            Result(3, brief: false, ComplianceResult.Unclear, ComplianceResult.Unclear),
        ]);

        Assert.Equal("1/2 (50%)", MarkdownReport.Accuracy(report, brief: true));
        Assert.Equal("1/1 (100%)", MarkdownReport.Accuracy(report, brief: false));
        Assert.Equal("n/a", MarkdownReport.Accuracy(new StrategyReport("s", [Result(1, true, ComplianceResult.Complies, ComplianceResult.Complies)]), brief: false));
    }

    [Fact]
    public void Render_ProducesRowPerCaseWithPassFailAndEscapedPipes()
    {
        var report = new StrategyReport("placeholder",
        [
            Result(1, brief: true, ComplianceResult.Complies, ComplianceResult.Complies, "Closed | ticket"),
            Result(2, brief: false, ComplianceResult.Deviates, ComplianceResult.Complies),
            new CaseResult(3, new LabCase("x", "y", ComplianceResult.Unclear), null, null, null, null, null, "Hugging Face returned HTTP 503."),
        ]);

        var md = MarkdownReport.Render([report], "facebook/bart-large-mnli", 0.5, new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));

        Assert.Contains("2026-10-03T12:00:00Z", md, StringComparison.Ordinal);
        Assert.Contains("| placeholder | 1/1 (100%) | 0/2 (0%) |", md, StringComparison.Ordinal);
        Assert.Contains("| 1 | **brief** Closed \\| ticket | COMPLIES | COMPLIES | MODEL | COMPLIES | 0.750 | 0.500 | pass |", md, StringComparison.Ordinal);
        Assert.Contains("| 2 | Did a thing | DEVIATES | COMPLIES | MODEL | COMPLIES | 0.750 | 0.500 | FAIL |", md, StringComparison.Ordinal);
        Assert.Contains("| 3 | x | UNCLEAR | — | — | — | — | — | ERROR: Hugging Face returned HTTP 503. |", md, StringComparison.Ordinal);
    }
}
