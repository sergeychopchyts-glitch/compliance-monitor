using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ComplianceMonitor.Api.RateLimiting;
using ComplianceMonitor.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ComplianceMonitor.Tests.Api;

public sealed class RateLimitingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class OneAtATimeApiFactory : ApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("RateLimiting:Analyze:PermitLimit", "1");
            builder.UseSetting("RateLimiting:Analyze:QueueLimit", "0");
        }
    }

    [Theory]
    [InlineData("/analyze", true)]
    [InlineData("/history", false)]
    [InlineData("/summary", false)]
    public void Policy_IsAttachedToAnalyzeOnly(string route, bool limited)
    {
        using var factory = new ApiFactory();
        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == route);

        var policy = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;

        Assert.Equal(limited ? AnalyzeRateLimitOptions.PolicyName : null, policy);
    }

    [Fact]
    public async Task Analyze_OverTheConcurrencyLimit_Returns429ButOtherEndpointsStillAnswer()
    {
        using var factory = new OneAtATimeApiFactory();
        factory.Model.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = factory.CreateClient();

        var first = client.PostAsJsonAsync(new Uri("/analyze", UriKind.Relative), new { action = "a", guideline = "g" }, Ct);
        for (var i = 0; i < 500 && factory.Model.Calls.Count == 0; i++)
        {
            await Task.Delay(10, Ct); // wait until the first request holds the only permit
        }

        using var second = await client.PostAsJsonAsync(new Uri("/analyze", UriKind.Relative), new { action = "b", guideline = "g" }, Ct);
        using var history = await client.GetAsync(new Uri("/history", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);
        var problem = JsonNode.Parse(await second.Content.ReadAsStringAsync(Ct))!;
        Assert.Equal("urn:compliance-monitor:problem:too-many-analyses", (string?)problem["type"]);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);

        factory.Model.Hold.SetResult();
        using var firstResponse = await first;
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Single(await factory.GetAnalysesAsync()); // the rejected request stored nothing
    }
}
