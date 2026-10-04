using System.Text.Json.Nodes;
using ComplianceMonitor.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace ComplianceMonitor.Tests.Endpoints;

public sealed class OpenApiTests
{
    /// <summary>OpenAPI is only mapped in Development.</summary>
    private sealed class DevelopmentApiFactory : ApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Development");
        }
    }

    private static async Task<JsonNode> GetDocument()
    {
        using var factory = new DevelopmentApiFactory();
        var json = await factory.CreateClient().GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!;
    }

    [Theory]
    [InlineData("ComplianceResult", new[] { "COMPLIES", "DEVIATES", "UNCLEAR" })]
    [InlineData("DecisionSource", new[] { "MODEL", "RULE", "LOW_CONFIDENCE" })]
    public async Task Document_ShowsEnumsAsStrings(string schema, string[] values)
    {
        var document = await GetDocument();

        var node = document["components"]?["schemas"]?[schema];
        Assert.NotNull(node);
        Assert.Equal(values, node["enum"]!.AsArray().Select(v => (string?)v));
    }

    [Fact]
    public async Task Document_SummaryListsAllThreeResultKeys()
    {
        var document = await GetDocument();

        var properties = document["components"]?["schemas"]?["ResultCounts"]?["properties"]?.AsObject();
        Assert.NotNull(properties);
        Assert.Equal(["COMPLIES", "DEVIATES", "UNCLEAR"], properties.Select(p => p.Key));
    }

    [Fact]
    public async Task Document_HasAllThreeEndpoints()
    {
        var paths = (await GetDocument())["paths"]!.AsObject();

        Assert.Equal(["/analyze", "/health", "/history", "/summary"], paths.Select(p => p.Key).Order());
    }

    [Theory]
    [InlineData("/analyze", "post")]
    [InlineData("/history", "get")]
    [InlineData("/summary", "get")]
    public async Task Document_GroupsAnalysisEndpointsUnderOneTag(string path, string method)
    {
        var operation = (await GetDocument())["paths"]![path]![method]!;

        Assert.Equal(["Analysis"], operation["tags"]!.AsArray().Select(t => (string?)t));
    }
}
