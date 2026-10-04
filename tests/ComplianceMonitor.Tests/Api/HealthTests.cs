using System.Net;
using ComplianceMonitor.Tests.TestSupport;

namespace ComplianceMonitor.Tests.Api;

public sealed class HealthTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(HttpStatusCode Status, string Body)> Get(ApiFactory factory, string path)
    {
        using var response = await factory.CreateClient().GetAsync(new Uri(path, UriKind.Relative), Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Live_AnswersWithoutCheckingAnything()
    {
        // Even with an unreachable database, the process is alive.
        using var factory = new ApiFactory { MigrateOnStartup = false, ConnectionStringOverride = UnreachableDatabase };

        Assert.Equal((HttpStatusCode.OK, "Healthy"), await Get(factory, "/health/live"));
    }

    [Fact]
    public async Task Ready_WhenTheDatabaseIsReachableAndMigrated()
    {
        using var factory = new ApiFactory();

        Assert.Equal((HttpStatusCode.OK, "Healthy"), await Get(factory, "/health/ready"));
    }

    [Fact]
    public async Task NotReady_WhenMigrationsArePending()
    {
        using var factory = new ApiFactory { MigrateOnStartup = false };

        Assert.Equal((HttpStatusCode.ServiceUnavailable, "Unhealthy"), await Get(factory, "/health/ready"));
    }

    [Fact]
    public async Task NotReady_WhenTheDatabaseCantBeReached()
    {
        using var factory = new ApiFactory { MigrateOnStartup = false, ConnectionStringOverride = UnreachableDatabase };

        Assert.Equal((HttpStatusCode.ServiceUnavailable, "Unhealthy"), await Get(factory, "/health/ready"));
    }

    [Fact]
    public async Task Ready_NeverCallsTheModel()
    {
        using var factory = new ApiFactory();

        await Get(factory, "/health/ready");

        Assert.Empty(factory.Model.Calls);
    }

    private static string UnreachableDatabase =>
        $"Data Source={Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid().ToString("N"), "x.db")};Mode=ReadWrite";
}
