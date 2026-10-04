using ComplianceMonitor.Infrastructure.Integrations.HuggingFace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ComplianceMonitor.Tests.TestSupport;

public static class TestClients
{
    /// <summary>The HF client over a fake handler, without the resilience pipeline (no retries).</summary>
    public static HuggingFaceZeroShotClient HuggingFace(HttpMessageHandler handler, HuggingFaceOptions? options = null) =>
        new(
            new HttpClient(handler),
            Options.Create(options ?? new HuggingFaceOptions { ApiToken = "token" }),
            NullLogger<HuggingFaceZeroShotClient>.Instance,
            TimeProvider.System);
}
