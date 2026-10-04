using System.Text.RegularExpressions;

namespace ComplianceMonitor.Application.Compliance.Rules;

/// <summary>
/// A guideline that sets an explicit frequency ("weekly", "every day", "once a month") is only fully met when
/// the action shows evidence of that frequency. Zero-shot NLI can't see a missing requirement, only a
/// contradiction: "Rebooted the server and checked logs" reads as compliant with "Servers must be rebooted
/// weekly..." even though nothing says it happened weekly.
/// Deliberately narrow: explicit frequency words and phrases, plus weekday names as evidence of "weekly".
/// No date arithmetic and no general rules engine.
/// </summary>
public static partial class TemporalRequirementRule
{
    private static readonly Dictionary<string, string> AdjectiveUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hourly"] = "hour",
        ["daily"] = "day",
        ["nightly"] = "day",
        ["weekly"] = "week",
        ["monthly"] = "month",
        ["quarterly"] = "quarter",
        ["annually"] = "year",
        ["annual"] = "year",
        ["yearly"] = "year",
    };

    /// <summary>The time units the guideline requires a frequency for ("week", "day", ...); empty when none.</summary>
    public static IReadOnlySet<string> RequiredUnits(string guideline)
    {
        ArgumentNullException.ThrowIfNull(guideline);
        return Units(guideline);
    }

    /// <summary>True when the guideline requires a frequency and the action shows no evidence of it.</summary>
    public static bool IsEvidenceMissing(string action, string guideline)
    {
        ArgumentNullException.ThrowIfNull(action);
        var required = RequiredUnits(guideline);
        if (required.Count == 0)
        {
            return false;
        }

        var evidenced = Units(action);
        if (Weekday().IsMatch(action))
        {
            evidenced.Add("week");
        }

        if (DailyTimeOfDay().IsMatch(action))
        {
            evidenced.Add("day");
        }

        return !required.IsSubsetOf(evidenced);
    }

    private static HashSet<string> Units(string text)
    {
        var units = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Adjective().Matches(text))
        {
            units.Add(AdjectiveUnits[match.Value]);
        }

        foreach (Match match in Phrase().Matches(text))
        {
            units.Add(match.Groups["unit"].Value.ToLowerInvariant());
        }

        return units;
    }

    [GeneratedRegex(@"\b(?:hourly|daily|nightly|weekly|monthly|quarterly|annually|annual|yearly)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Adjective();

    // "every week", "every 2 weeks", "every other day", "each month", "once a year", "once per week", "per quarter"
    [GeneratedRegex(
        @"\b(?:every(?:\s+(?:\d+|other|single))?|each|once\s+(?:a|an|per|every)|per)\s+(?<unit>hour|day|week|month|quarter|year)s?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Phrase();

    [GeneratedRegex(@"\b(?:monday|tuesday|wednesday|thursday|friday|saturday|sunday)s?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Weekday();

    [GeneratedRegex(@"\bevery\s+(?:morning|evening|night)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DailyTimeOfDay();
}
