namespace ComplianceMonitor.Infrastructure.Caching;

internal static class CacheKeys
{
    /// <summary>Versioned, so a change to the cached shape never reads an old entry.</summary>
    public const string AnalysisSummary = "analysis:summary:v1";
}
