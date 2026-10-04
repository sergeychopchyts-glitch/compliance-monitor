namespace ComplianceMonitor.Client;

public enum ClientCommand
{
    Demo,
    Analyze,
    RunCases,
    History,
    Summary,
    Help,
}

public sealed record ClientArguments(
    ClientCommand Command,
    string? BaseUrl = null,
    string? Action = null,
    string? Guideline = null,
    int? Limit = null,
    string? Result = null)
{
    public static readonly string[] Results = ["COMPLIES", "DEVIATES", "UNCLEAR"];

    private static readonly Dictionary<string, ClientCommand> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["demo"] = ClientCommand.Demo,
        ["analyze"] = ClientCommand.Analyze,
        ["run-cases"] = ClientCommand.RunCases,
        ["history"] = ClientCommand.History,
        ["summary"] = ClientCommand.Summary,
        ["help"] = ClientCommand.Help,
    };

    // Options each command accepts; --base-url and --help are accepted everywhere.
    private static readonly Dictionary<ClientCommand, string[]> AllowedOptions = new()
    {
        [ClientCommand.Demo] = [],
        [ClientCommand.Analyze] = ["--action", "--guideline"],
        [ClientCommand.RunCases] = [],
        [ClientCommand.History] = ["--limit", "--result"],
        [ClientCommand.Summary] = [],
        [ClientCommand.Help] = [],
    };

    /// <summary>Hand-rolled on purpose: five commands do not need a parsing library.</summary>
    /// <returns>The arguments, or a usage error message.</returns>
    public static (ClientArguments? Arguments, string? Error) Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var command = ClientCommand.Demo;
        var commandSeen = false;
        var options = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
            {
                return (new ClientArguments(ClientCommand.Help), null);
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    return (null, $"{arg} needs a value.");
                }

                if (!options.TryAdd(arg, args[++i]))
                {
                    return (null, $"{arg} was given more than once.");
                }
            }
            else if (!commandSeen && Commands.TryGetValue(arg, out var parsed))
            {
                command = parsed;
                commandSeen = true;
            }
            else
            {
                return (null, commandSeen ? $"Unexpected argument '{arg}'." : $"Unknown command '{arg}'.");
            }
        }

        foreach (var option in options.Keys.Where(o => o != "--base-url" && !AllowedOptions[command].Contains(o)))
        {
            return (null, $"{option} is not an option of '{Name(command)}'.");
        }

        options.TryGetValue("--base-url", out var baseUrl);
        options.TryGetValue("--action", out var action);
        options.TryGetValue("--guideline", out var guideline);
        options.TryGetValue("--result", out var result);

        if (command == ClientCommand.Analyze && (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(guideline)))
        {
            return (null, "analyze needs --action \"...\" and --guideline \"...\".");
        }

        int? limit = null;
        if (options.TryGetValue("--limit", out var limitText))
        {
            if (!int.TryParse(limitText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsedLimit))
            {
                return (null, "--limit must be a whole number.");
            }

            limit = parsedLimit;
        }

        if (result is not null)
        {
            result = Results.FirstOrDefault(r => r.Equals(result, StringComparison.OrdinalIgnoreCase));
            if (result is null)
            {
                return (null, $"--result must be one of {string.Join(", ", Results)}.");
            }
        }

        return (new ClientArguments(command, baseUrl, action, guideline, limit, result), null);
    }

    private static string Name(ClientCommand command) => Commands.First(c => c.Value == command).Key;
}
