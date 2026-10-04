using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ComplianceMonitor.LabelLab;

/// <summary>
/// Outermost handler: answers from <c>.cache/hf/</c> when it can, and spaces out the calls that
/// reach the network. Only 200 responses are cached, so failures are retried on the next run.
/// The key hashes the URL and the body: the same prompt sent to a different model is a different entry.
/// The Authorization header is not part of the key and is never written to disk.
/// </summary>
public sealed class CachingThrottlingHandler(string cacheDirectory, TimeSpan minInterval, TimeProvider timeProvider)
    : DelegatingHandler
{
    private readonly string _cacheDirectory = cacheDirectory;
    private readonly TimeSpan _minInterval = minInterval;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _lastNetworkCall;

    public int CacheHits { get; private set; }

    public int NetworkCalls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = Path.Combine(_cacheDirectory, CacheKey(request.RequestUri, body) + ".json");

        if (File.Exists(path))
        {
            CacheHits++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(await File.ReadAllTextAsync(path, cancellationToken), Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_lastNetworkCall is { } last)
            {
                var wait = _minInterval - (_timeProvider.GetUtcNow() - last);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _timeProvider, cancellationToken);
                }
            }

            _lastNetworkCall = _timeProvider.GetUtcNow();
            NetworkCalls++;
        }
        finally
        {
            _gate.Release();
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            Directory.CreateDirectory(_cacheDirectory);
            await File.WriteAllTextAsync(path, responseBody, cancellationToken);
            response.Content = new StringContent(responseBody, Encoding.UTF8, "application/json");
        }

        return response;
    }

    public static string CacheKey(Uri? uri, string body)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{uri}\n{body}"));
        return Convert.ToHexStringLower(bytes);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gate.Dispose();
        }

        base.Dispose(disposing);
    }
}
