using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace;
using ComplianceMonitor.Infrastructure.Persistence;
using ComplianceMonitor.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ComplianceMonitor.Tests;

/// <summary>CLAUDE.md hard constraints that are about configuration and source code rather than behaviour.</summary>
public sealed partial class ConfigurationTests
{
    private static readonly string[] SourceFolders = ["src", "tools"];

    private static string RepoRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ComplianceMonitor.slnx")))
                {
                    return dir.FullName;
                }
            }

            throw new InvalidOperationException("Repository root (ComplianceMonitor.slnx) not found.");
        }
    }

    private static IEnumerable<string> SourceFiles(string pattern) =>
        SourceFolders
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(RepoRoot, folder), pattern, SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static IEnumerable<string> PropertyNames(JsonNode? node) => node switch
    {
        JsonObject obj => obj.SelectMany(p => PropertyNames(p.Value).Prepend(p.Key)),
        JsonArray array => array.SelectMany(PropertyNames),
        _ => [],
    };

    [Fact]
    public void AppSettings_NeverContainTheApiToken()
    {
        var files = SourceFiles("appsettings*.json").ToList();

        Assert.NotEmpty(files);
        Assert.All(files, file =>
            Assert.DoesNotContain(
                PropertyNames(JsonNode.Parse(File.ReadAllText(file))),
                name => name.Equals(nameof(HuggingFaceOptions.ApiToken), StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void AppSettings_DefaultConnectionStringIsComplianceDb()
    {
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot, "src", "ComplianceMonitor.Api", "appsettings.json")))!;

        Assert.Equal("Data Source=compliance.db", (string?)settings["ConnectionStrings"]?[ComplianceDbContext.ConnectionStringName]);
    }

    [Fact]
    public void Api_UsesTheConnectionStringFromConfiguration()
    {
        using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<ComplianceDbContext>();

        Assert.Equal(factory.ConnectionString, db.Database.GetConnectionString());
    }

    [Fact]
    public void Api_ReadsTheTokenFromTheEnvironmentVariable()
    {
        const string variable = "HuggingFace__ApiToken";
        const string value = "env-token-not-real";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, value);
        try
        {
            using var factory = new EnvironmentTokenApiFactory();

            Assert.Equal(value, factory.Services.GetRequiredService<IOptions<HuggingFaceOptions>>().Value.ApiToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public void Source_NeverReadsTheSystemClockDirectly()
    {
        var offenders = SourceFiles("*.cs")
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => File.ReadLines(path).Select((line, i) => (path, line, number: i + 1)))
            .Where(x => SystemClock().IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(RepoRoot, x.path)}:{x.number}")
            .ToList();

        Assert.True(offenders.Count == 0, "Use the injected TimeProvider instead of: " + string.Join(", ", offenders));
    }

    [GeneratedRegex(@"\bDateTime(Offset)?\.(Now|UtcNow|Today)\b")]
    private static partial Regex SystemClock();

    [Theory]
    [InlineData("1.5")]
    [InlineData("-0.1")]
    [InlineData("NaN")]
    public void Startup_InvalidConfidenceThreshold_FailsNamingTheKey(string threshold)
    {
        using var factory = new ThresholdApiFactory(threshold);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Compliance:ConfidenceThreshold must be between 0 and 1.", ex.ToString(), StringComparison.Ordinal);
    }

    private sealed class ThresholdApiFactory(string threshold) : ApiFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Compliance:ConfidenceThreshold", threshold);
        }
    }

    /// <summary>Sets no token itself, so the app's own sources (here, the environment) supply it.</summary>
    private sealed class EnvironmentTokenApiFactory : ApiFactory
    {
        protected override string? ApiToken => null;
    }
}
