namespace ComplianceMonitor.Client;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // let the runner finish cleanly instead of killing the process
            cts.Cancel();
        };

        using var handler = new SocketsHttpHandler();
        return await ClientApp.RunAsync(args, Environment.GetEnvironmentVariable, handler, Console.Out, Console.Error, cts.Token);
    }
}
