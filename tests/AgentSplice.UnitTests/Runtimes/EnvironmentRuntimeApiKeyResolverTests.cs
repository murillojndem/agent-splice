using System.Globalization;
using AgentSplice.Application.Diagnostics;
using AgentSplice.Infrastructure.Runtimes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentSplice.UnitTests.Runtimes;

/// <summary>
/// What the gateway says about a runtime credential it cannot find (docs/SPECIFICATION.md FR-DATA-010,
/// docs/OBSERVABILITY.md "Structured logs").
/// </summary>
public sealed class EnvironmentRuntimeApiKeyResolverTests
{
    [Fact]
    public void A_key_variable_that_is_unset_is_logged_once_as_information()
    {
        // Information, not a warning: the shipped settings name LM_STUDIO_API_KEY, which most machines
        // leave unset, and a warning at every start reads as a misconfiguration the user never made.
        var entries = new List<(EventId EventId, LogLevel Level)>();
        var resolver = new EnvironmentRuntimeApiKeyResolver(new CapturingLogger<EnvironmentRuntimeApiKeyResolver>(entries));
        var target = RuntimeTargetTests.Target("http://127.0.0.1:1234/v1", apiKeyEnvironmentVariable: UniqueVariable());

        Assert.False(resolver.Resolve(target).HasValue);
        Assert.False(resolver.Resolve(target).HasValue);

        var entry = Assert.Single(entries);
        Assert.Equal(GatewayEventIds.RuntimeCredentialMissing, entry.EventId);
        Assert.Equal(LogLevel.Information, entry.Level);
    }

    [Fact]
    public void A_key_variable_that_is_set_resolves_and_logs_nothing()
    {
        var variable = UniqueVariable();
        Environment.SetEnvironmentVariable(variable, "sk-test-not-a-real-key");

        try
        {
            var entries = new List<(EventId EventId, LogLevel Level)>();
            var resolver = new EnvironmentRuntimeApiKeyResolver(new CapturingLogger<EnvironmentRuntimeApiKeyResolver>(entries));

            Assert.True(resolver.Resolve(RuntimeTargetTests.Target("http://127.0.0.1:1234/v1", apiKeyEnvironmentVariable: variable)).HasValue);
            Assert.Empty(entries);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void A_runtime_that_names_no_key_variable_logs_nothing()
    {
        var entries = new List<(EventId EventId, LogLevel Level)>();
        var resolver = new EnvironmentRuntimeApiKeyResolver(new CapturingLogger<EnvironmentRuntimeApiKeyResolver>(entries));

        Assert.False(resolver.Resolve(RuntimeTargetTests.Target("http://127.0.0.1:1234/v1")).HasValue);
        Assert.Empty(entries);
    }

    /// <summary>A variable name no other test and no machine sets, so parallel tests never share one.</summary>
    private static string UniqueVariable() =>
        "AGENTSPLICE_TEST_KEY_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Records the identity and level of every entry, which is what the assertions are about.</summary>
    private sealed class CapturingLogger<TCategory> : ILogger<TCategory>
    {
        private readonly List<(EventId EventId, LogLevel Level)> entries;

        internal CapturingLogger(List<(EventId EventId, LogLevel Level)> entries) => this.entries = entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Add((eventId, logLevel));
    }
}
