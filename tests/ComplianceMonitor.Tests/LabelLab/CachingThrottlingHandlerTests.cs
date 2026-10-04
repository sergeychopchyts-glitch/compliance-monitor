using System.Net;
using System.Text;
using ComplianceMonitor.LabelLab;
using ComplianceMonitor.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace ComplianceMonitor.Tests.LabelLab;

public sealed class CachingThrottlingHandlerTests : IDisposable
{
    private static readonly Uri ModelUrl = new("https://router.huggingface.co/hf-inference/models/facebook/bart-large-mnli");
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(300);

    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), "labellab-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new();

    public void Dispose()
    {
        if (Directory.Exists(_cacheDirectory))
        {
            Directory.Delete(_cacheDirectory, recursive: true);
        }
    }

    private (HttpClient Client, CachingThrottlingHandler Handler) Create(FakeHttpMessageHandler inner)
    {
        var handler = new CachingThrottlingHandler(_cacheDirectory, Interval, _time) { InnerHandler = inner };
        return (new HttpClient(handler), handler);
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string body, Uri? uri = null) =>
        client.PostAsync(uri ?? ModelUrl, new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

    [Fact]
    public async Task SameRequestTwice_SecondIsServedFromDiskWithoutNetwork()
    {
        var inner = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, """[{"label":"a","score":1}]""");
        var (client, handler) = Create(inner);
        using var _ = client;

        using var first = await Post(client, """{"inputs":"x"}""");
        using var second = await Post(client, """{"inputs":"x"}""");

        Assert.Single(inner.Requests);
        Assert.Equal(1, handler.NetworkCalls);
        Assert.Equal(1, handler.CacheHits);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("""[{"label":"a","score":1}]""", await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var file = Assert.Single(Directory.GetFiles(_cacheDirectory));
        Assert.Equal(CachingThrottlingHandler.CacheKey(ModelUrl, """{"inputs":"x"}""") + ".json", Path.GetFileName(file));
    }

    [Fact]
    public async Task NonSuccessResponse_IsNotCached()
    {
        var inner = FakeHttpMessageHandler.Returning(HttpStatusCode.ServiceUnavailable);
        var (client, _) = Create(inner);
        using var __ = client;

        using var first = await Post(client, "{}");
        _time.Advance(Interval);
        using var second = await Post(client, "{}");

        Assert.Equal(2, inner.Requests.Count);
        Assert.False(Directory.Exists(_cacheDirectory) && Directory.EnumerateFiles(_cacheDirectory).Any());
    }

    [Fact]
    public void CacheKey_DependsOnUrlAndBody()
    {
        var key = CachingThrottlingHandler.CacheKey(ModelUrl, "{}");

        Assert.Equal(key, CachingThrottlingHandler.CacheKey(ModelUrl, "{}"));
        Assert.NotEqual(key, CachingThrottlingHandler.CacheKey(ModelUrl, "{ }"));
        Assert.NotEqual(key, CachingThrottlingHandler.CacheKey(new Uri("https://example.com/other-model"), "{}"));
        Assert.Matches("^[0-9a-f]{64}$", key);
    }

    [Fact]
    public async Task UncachedCalls_AreSpacedByInterval()
    {
        var inner = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "[]");
        var (client, _) = Create(inner);
        using var __ = client;

        using var first = await Post(client, "one");
        var second = Post(client, "two");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        _time.Advance(Interval - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        using var response = await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, inner.Requests.Count);
    }

    [Fact]
    public async Task CacheHit_IsNotThrottled()
    {
        var inner = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "[]");
        var (client, _) = Create(inner);
        using var __ = client;

        using var first = await Post(client, "one");
        using var hit = await Post(client, "one").WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Single(inner.Requests);
    }
}
