using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AgentSplice.TestSupport.Logging;

/// <summary>
/// Captures every log record a host emits, including structured state and active scopes.
/// </summary>
/// <remarks>
/// The formatted message alone is not enough to prove that content never leaks. A structured value
/// or a scope property reaches a log sink just as surely, and either can carry a prompt or a
/// credential into a file an operator later shares.
/// </remarks>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<CapturedLogRecord> records = new();
    private IExternalScopeProvider? scopeProvider;

    /// <summary>Every record captured so far.</summary>
    public IReadOnlyList<CapturedLogRecord> Records => records.ToArray();

    /// <summary>Every string that reached a log, from any channel.</summary>
    /// <remarks>
    /// Flattened so an assertion covers messages, structured values, and scopes at once rather than
    /// checking the one channel the author happened to think of.
    /// </remarks>
    public IEnumerable<string> AllText => Records.SelectMany(record => record.AllText());

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        new CapturingLogger(categoryName, records, () => scopeProvider);

    /// <inheritdoc />
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
        this.scopeProvider = scopeProvider;

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release: records are held in memory for the lifetime of the test.
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string category;
        private readonly ConcurrentQueue<CapturedLogRecord> records;
        private readonly Func<IExternalScopeProvider?> scopeProvider;

        internal CapturingLogger(
            string category,
            ConcurrentQueue<CapturedLogRecord> records,
            Func<IExternalScopeProvider?> scopeProvider)
        {
            this.category = category;
            this.records = records;
            this.scopeProvider = scopeProvider;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            scopeProvider()?.Push(state);

        // Everything is captured, at every level. The assertion that matters is that content is
        // absent even at the most verbose setting, not merely filtered out by the default one.
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            var values = new List<string>();

            if (state is IEnumerable<KeyValuePair<string, object?>> structured)
            {
                foreach (var (key, value) in structured)
                {
                    values.Add($"{key}={value}");
                }
            }

            var scopes = new List<string>();

            scopeProvider()?.ForEachScope(
                (scope, collected) => collected.Add(Describe(scope)),
                scopes);

            records.Enqueue(new CapturedLogRecord(
                logLevel,
                category,
                eventId,
                formatter(state, exception),
                exception?.ToString(),
                values,
                scopes));
        }

        private static string Describe(object? scope)
        {
            if (scope is IEnumerable<KeyValuePair<string, object?>> structured)
            {
                return string.Join(",", structured.Select(pair => $"{pair.Key}={pair.Value}"));
            }

            return scope?.ToString() ?? string.Empty;
        }
    }
}
