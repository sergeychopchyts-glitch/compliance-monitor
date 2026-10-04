using System.Text.RegularExpressions;

namespace ComplianceMonitor.Application.Compliance.Rules;

/// <summary>
/// When the guideline itself states that there is no guideline, there is nothing to judge against: UNCLEAR,
/// without asking the model. The whole guideline must be that statement, optionally with a short qualifier
/// ("for this case", "to Station 3"). A real guideline that merely mentions the phrase ("If no guideline exists
/// for a station, escalate...") goes to the model.
/// </summary>
public static partial class NoGuidelineRule
{
    public static bool Matches(string guideline)
    {
        ArgumentNullException.ThrowIfNull(guideline);
        return Statement().IsMatch(guideline.Trim());
    }

    // [there is|are] no [applicable] guideline(s) [exist(s)|apply|applies] [for|to|in|at|on + up to 4 words] [.|!]
    [GeneratedRegex(
        @"^(?:there\s+(?:is|are)\s+)?no\s+(?:applicable\s+)?guidelines?(?:\s+(?:exists?|appl(?:y|ies)))?(?:\s+(?:for|to|in|at|on)(?:\s+[\w#-]+){1,4})?\s*[.!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Statement();
}
