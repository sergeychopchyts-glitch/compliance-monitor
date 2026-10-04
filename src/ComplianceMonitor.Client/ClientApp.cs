namespace ComplianceMonitor.Client;

/// <summary>
/// The whole client behind one call, with the outside world passed in (handler, environment, writers),
/// so tests drive exactly what a user runs.
/// </summary>
public static class ClientApp
{
    public const string Usage = """
        Compliance Monitor client

        Usage: dotnet run --project src/ComplianceMonitor.Client -- [command] [options]

        Commands:
          demo                  run-cases, then history, then summary (the default)
          analyze               classify one action:  --action "<text>" --guideline "<text>"
          run-cases             send the brief's four cases; print expected vs actual and PASS/FAIL
          history               stored analyses, newest first:  [--limit <1-200>] [--result COMPLIES|DEVIATES|UNCLEAR]
          summary               totals by result

        Options:
          --base-url <url>      API address; default $COMPLIANCE_API_URL, else http://localhost:5080
          -h, --help            show this help

        Exit codes:
          0    success
          1    a brief case did not match its expected result (run-cases, demo)
          2    the API could not be reached, or did not respond in time
          3    the API returned an error
          64   bad usage
          130  cancelled (Ctrl+C)
        """;

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        Func<string, string?> getEnvironmentVariable,
        HttpMessageHandler handler,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var (arguments, usageError) = ClientArguments.Parse(args);
        if (arguments is null)
        {
            error.WriteLine(usageError);
            error.WriteLine();
            error.WriteLine(Usage);
            return ExitCodes.Usage;
        }

        if (arguments.Command == ClientCommand.Help)
        {
            output.WriteLine(Usage);
            return ExitCodes.Ok;
        }

        var baseUrlText = arguments.BaseUrl ?? getEnvironmentVariable(ClientDefaults.BaseUrlVariable);
        Uri baseUrl;
        if (string.IsNullOrWhiteSpace(baseUrlText))
        {
            baseUrl = ClientDefaults.BaseUrl;
        }
        else if (!Uri.TryCreate(baseUrlText, UriKind.Absolute, out var parsed) || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            error.WriteLine($"'{baseUrlText}' is not an http(s) URL.");
            return ExitCodes.Usage;
        }
        else
        {
            baseUrl = parsed;
        }

        // A trailing slash keeps any path in the base URL when relative paths are resolved against it.
        baseUrl = new Uri(baseUrl.AbsoluteUri.TrimEnd('/') + "/");

        using var httpClient = new HttpClient(handler, disposeHandler: false) { BaseAddress = baseUrl, Timeout = ClientDefaults.Timeout };
        var runner = new CommandRunner(new ComplianceApiClient(httpClient), output, error);
        return await runner.RunAsync(arguments, cancellationToken);
    }
}
