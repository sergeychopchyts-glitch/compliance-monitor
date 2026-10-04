using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ComplianceMonitor.Tests.Infrastructure;

/// <summary>
/// Hosts the API in-process. The "Testing" environment keeps user-secrets out, and the in-memory
/// source is added last so it also wins over a HuggingFace__ApiToken environment variable.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string FakeToken = "test-token-not-real";

    protected virtual string ApiToken => FakeToken;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HuggingFace:ApiToken"] = ApiToken,
        }));
    }
}
