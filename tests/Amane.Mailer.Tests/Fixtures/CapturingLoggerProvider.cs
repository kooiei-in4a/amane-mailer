using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Amane.Mailer.Tests.Fixtures;

/// <summary>
/// Captures formatted log lines and structured state values for PII/secret canary
/// assertions on Worker (and related) logging paths.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    public void Clear()
    {
        while (_entries.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<CapturedLogEntry> Snapshot() => _entries.ToArray();

    public string JoinedOutput()
    {
        var entries = Snapshot();
        return string.Join(
            Environment.NewLine,
            entries.Select(static entry =>
            {
                var stateText = string.Join(
                    " | ",
                    entry.State.Select(static pair => $"{pair.Key}={pair.Value}"));
                return $"[{entry.Category}] {entry.Level}: {entry.FormattedMessage} :: {stateText}";
            }));
    }

    public ILogger CreateLogger(string categoryName) =>
        new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(
        string categoryName,
        ConcurrentQueue<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state as IEnumerable<KeyValuePair<string, object?>>
                ?? [];
            entries.Enqueue(new CapturedLogEntry(
                categoryName,
                logLevel,
                eventId,
                formatter(state, exception),
                properties.GroupBy(static pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        static group => group.Key,
                        static group => group.Last().Value?.ToString() ?? string.Empty,
                        StringComparer.Ordinal)));
        }
    }
}

public sealed record CapturedLogEntry(
    string Category,
    LogLevel Level,
    EventId EventId,
    string FormattedMessage,
    IReadOnlyDictionary<string, string> State);
