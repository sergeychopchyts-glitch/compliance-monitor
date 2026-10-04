using ComplianceMonitor.Application.Compliance.Rules;

namespace ComplianceMonitor.Tests.Application;

public sealed class NoGuidelineRuleTests
{
    [Theory]
    [InlineData("No guidelines exist for this case.")]
    [InlineData("no guideline exists")]
    [InlineData("NO   GUIDELINES\tEXIST")]
    [InlineData("  No guideline.  ")]
    [InlineData("There is no applicable guideline.")]
    [InlineData("No guideline applies to Station 3")]
    public void Matches_AWholeNoGuidelineStatement(string guideline) =>
        Assert.True(NoGuidelineRule.Matches(guideline));

    [Theory]
    [InlineData("All closed tickets must include a confirmation email")]
    [InlineData("Guidelines exist for every station")]
    [InlineData("No ticket may be closed without a guideline review")]
    [InlineData("If no guideline exists for a station, escalate to the supervisor")]
    [InlineData("When no guidelines apply, the operator must log the step")]
    [InlineData("No guidelines exist for this case, so stop the line")]
    [InlineData("No guidelines exist for this case and operators must stop the line")]
    [InlineData("No guideline exists yet; ask a supervisor")]
    public void DoesNotMatch_AGuidelineThatMerelyMentionsThePhrase(string guideline) =>
        Assert.False(NoGuidelineRule.Matches(guideline));
}

public sealed class TemporalRequirementRuleTests
{
    private const string WeeklyReboot = "Servers must be rebooted weekly and logs reviewed after restart";

    [Theory]
    [InlineData(WeeklyReboot, new[] { "week" })]
    [InlineData("Backups must run every day", new[] { "day" })]
    [InlineData("Audit access logs once a month and rotate keys annually", new[] { "month", "year" })]
    [InlineData("Inspect the guard every 2 weeks", new[] { "week" })]
    [InlineData("Refunds must be issued within 5 business days of the request", new string[0])]
    [InlineData("All closed tickets must include a confirmation email", new string[0])]
    [InlineData("Log one entry per request", new string[0])]
    public void RequiredUnits_FindsExplicitFrequenciesOnly(string guideline, string[] expected) =>
        Assert.Equal(expected.Order(), TemporalRequirementRule.RequiredUnits(guideline).Order());

    [Theory]
    [InlineData("Rebooted the server and checked logs")]                              // brief Case 3
    [InlineData("Rebooted the server monthly and checked logs")]                      // a different frequency
    [InlineData("Rebooted the server yesterday and checked logs")]                    // a time, not a frequency
    public void EvidenceMissing_WhenTheActionDoesNotShowTheRequiredFrequency(string action) =>
        Assert.True(TemporalRequirementRule.IsEvidenceMissing(action, WeeklyReboot));

    [Theory]
    [InlineData("Performed the weekly server reboot and reviewed the logs afterwards")]
    [InlineData("Rebooted the server as it is every week and checked logs")]
    [InlineData("Performed the scheduled Monday reboot of the server and reviewed the logs afterwards")]
    [InlineData("Reboots the servers on Sundays, then reviews the logs")]
    [InlineData("Rebooted the server once a week and checked logs")]
    public void EvidencePresent_WhenTheActionStatesTheFrequency(string action) =>
        Assert.False(TemporalRequirementRule.IsEvidenceMissing(action, WeeklyReboot));

    [Theory]
    [InlineData("Ran the backup every morning", "Backups must run daily", false)]
    [InlineData("Ran the nightly backup", "Backups must run daily", false)]
    [InlineData("Ran the backup", "Backups must run daily", true)]
    [InlineData("Audited access monthly", "Audit access logs once a month and rotate keys annually", true)] // annual missing
    [InlineData("Closed the ticket", "All closed tickets must include a confirmation email", false)]       // no frequency
    public void EvidenceMissing_OtherUnits(string action, string guideline, bool missing) =>
        Assert.Equal(missing, TemporalRequirementRule.IsEvidenceMissing(action, guideline));
}
