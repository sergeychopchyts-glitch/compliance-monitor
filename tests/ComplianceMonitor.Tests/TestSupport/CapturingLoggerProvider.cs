using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ComplianceMonitor.Tests.TestSupport;

public sealed record LogEntry(string Category, LogLevel Level, string Message, IReadOnlyDictionary<string, object?> State);

/// <summary>Captures every log entry from every category, including structured state.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
            entries.Enqueue(new LogEntry(
                category,
                logLevel,
                formatter(state, exception) + (exception is null ? "" : " " + exception),
                values.ToDictionary(v => v.Key, v => v.Value)));
        }
    }
}
