using System.Globalization;

namespace ComplianceMonitor.Client;

public static class ExitCodes
{
    public const int Ok = 0;
    public const int CaseFailed = 1;
    public const int ApiUnreachable = 2;
    public const int ApiError = 3;
    public const int Usage = 64;
    public const int Cancelled = 130;
}

public sealed record BriefCase(int Number, string Action, string Guideline, string Expected);

/// <summary>Runs one command and turns every failure into a message and an exit code. Never prints a stack trace.</summary>
public sealed class CommandRunner(ComplianceApiClient api, TextWriter output, TextWriter error)
{
    public const string StartHint = "dotnet run --project src/ComplianceMonitor.Api";

    /// <summary>The four cases from docs/exercise.md.</summary>
    public static readonly BriefCase[] BriefCases =
    [
        new(1, "Closed ticket #48219 and sent confirmation email", "All closed tickets must include a confirmation email", "COMPLIES"),
        new(2, "Closed ticket #48219 without sending confirmation email", "All closed tickets must include a confirmation email", "DEVIATES"),
        new(3, "Rebooted the server and checked logs", "Servers must be rebooted weekly and logs reviewed after restart", "DEVIATES"),
        new(4, "Skipped torque confirmation at Station 3", "No guidelines exist for this case.", "UNCLEAR"),
    ];

    private readonly ComplianceApiClient _api = api;
    private readonly TextWriter _out = output;
    private readonly TextWriter _err = error;

    public async Task<int> RunAsync(ClientArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        try
        {
            return arguments.Command switch
            {
                ClientCommand.Analyze => await AnalyzeAsync(arguments.Action!, arguments.Guideline!, cancellationToken),
                ClientCommand.RunCases => await RunCasesAsync(cancellationToken),
                ClientCommand.History => await HistoryAsync(arguments.Limit, arguments.Result, cancellationToken),
                ClientCommand.Summary => await SummaryAsync(cancellationToken),
                _ => await DemoAsync(cancellationToken),
            };
        }
        catch (ApiUnreachableException ex)
        {
            WriteUnreachable(ex);
            return ExitCodes.ApiUnreachable;
        }
        catch (ApiErrorException ex)
        {
            WriteApiError(ex);
            return ExitCodes.ApiError;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _err.WriteLine("Cancelled.");
            return ExitCodes.Cancelled;
        }
    }

    private async Task<int> DemoAsync(CancellationToken cancellationToken)
    {
        var casesExit = await RunCasesAsync(cancellationToken);
        _out.WriteLine();
        await HistoryAsync(limit: 10, result: null, cancellationToken);
        _out.WriteLine();
        await SummaryAsync(cancellationToken);
        return casesExit;
    }

    private async Task<int> AnalyzeAsync(string action, string guideline, CancellationToken cancellationToken)
    {
        var item = await _api.AnalyzeAsync(action, guideline, cancellationToken);
        _out.WriteLine(Invariant($"Result:      {item.Result}"));
        _out.WriteLine(Invariant($"Confidence:  {item.Confidence:0.00}"));
        _out.WriteLine(Invariant($"Decided by:  {item.DecidedBy}"));
        _out.WriteLine(Invariant($"Timestamp:   {item.Timestamp}"));
        _out.WriteLine(Invariant($"Id:          {item.Id}"));
        return ExitCodes.Ok;
    }

    // An API error on one case is reported and the rest still run; unreachable stops everything (it propagates).
    private async Task<int> RunCasesAsync(CancellationToken cancellationToken)
    {
        _out.WriteLine("Brief cases");
        var passed = 0;
        var errored = false;
        foreach (var briefCase in BriefCases)
        {
            try
            {
                var item = await _api.AnalyzeAsync(briefCase.Action, briefCase.Guideline, cancellationToken);
                var pass = item.Result == briefCase.Expected;
                passed += pass ? 1 : 0;
                _out.WriteLine(Invariant(
                    $"  Case {briefCase.Number}  expected {briefCase.Expected,-8}  actual {item.Result,-8}  ({item.Confidence:0.00}, {item.DecidedBy})  {(pass ? "PASS" : "FAIL")}"));
            }
            catch (ApiErrorException ex)
            {
                errored = true;
                _out.WriteLine(Invariant($"  Case {briefCase.Number}  expected {briefCase.Expected,-8}  ERROR"));
                WriteApiError(ex);
            }
        }

        _out.WriteLine(Invariant($"{passed}/{BriefCases.Length} cases passed."));
        return errored ? ExitCodes.ApiError : passed == BriefCases.Length ? ExitCodes.Ok : ExitCodes.CaseFailed;
    }

    private async Task<int> HistoryAsync(int? limit, string? result, CancellationToken cancellationToken)
    {
        var items = await _api.GetHistoryAsync(limit, result, cancellationToken);
        _out.WriteLine(Invariant($"History ({items.Length} shown, newest first)"));
        if (items.Length == 0)
        {
            _out.WriteLine("  No analyses yet.");
        }

        foreach (var item in items)
        {
            _out.WriteLine(Invariant(
                $"  #{item.Id,-4} {item.Timestamp}  {item.Result,-8} {item.Confidence:0.00}  {Shorten(item.Action)}  |  {Shorten(item.Guideline)}"));
        }

        return ExitCodes.Ok;
    }

    private async Task<int> SummaryAsync(CancellationToken cancellationToken)
    {
        var summary = await _api.GetSummaryAsync(cancellationToken);
        _out.WriteLine("Summary");
        _out.WriteLine(Invariant($"  Total     {summary.Total}"));
        foreach (var result in ClientArguments.Results)
        {
            _out.WriteLine(Invariant($"  {result,-9} {summary.ByResult.GetValueOrDefault(result)}"));
        }

        return ExitCodes.Ok;
    }

    private void WriteUnreachable(ApiUnreachableException ex)
    {
        if (ex.TimedOut)
        {
            _err.WriteLine(Invariant(
                $"The API at {ex.BaseUrl} did not respond within {ClientDefaults.Timeout.TotalSeconds:0} s."));
        }
        else
        {
            _err.WriteLine($"Can't reach the API at {ex.BaseUrl}. Start it with: {StartHint}");
        }
    }

    private void WriteApiError(ApiErrorException ex)
    {
        if (ex.Title is null && ex.Detail is null && ex.FieldErrors.Count == 0)
        {
            _err.WriteLine(Invariant($"The API returned {ex.StatusCode} with a response the client could not read."));
            return;
        }

        if (ex.FieldErrors.Count > 0)
        {
            _err.WriteLine(Invariant($"The API rejected the request ({ex.StatusCode}):"));
            foreach (var (field, messages) in ex.FieldErrors)
            {
                foreach (var message in messages)
                {
                    _err.WriteLine($"  {field}: {message}");
                }
            }

            return;
        }

        _err.WriteLine(Invariant($"The API returned {ex.StatusCode}: {ex.Title ?? "error"}"));
        if (!string.IsNullOrWhiteSpace(ex.Detail))
        {
            _err.WriteLine($"  {ex.Detail}");
        }

        if (ex.RetryAfter is { } retryAfter)
        {
            _err.WriteLine(Invariant($"  Retry after {Math.Ceiling(retryAfter.TotalSeconds):0} s."));
        }
    }

    private static string Shorten(string text) => text.Length <= 40 ? text : string.Concat(text.AsSpan(0, 39), "…");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
