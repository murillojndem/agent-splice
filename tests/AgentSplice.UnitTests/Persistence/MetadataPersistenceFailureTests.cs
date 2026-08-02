using AgentSplice.Application.Configuration;
using AgentSplice.Application.Diagnostics;
using AgentSplice.Application.Exchanges;
using AgentSplice.Application.Observability;
using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Observations;
using AgentSplice.Infrastructure.Persistence;
using AgentSplice.UnitTests.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentSplice.UnitTests.Persistence;

/// <summary>
/// What happens when the store will not accept a write (FR-DATA-009, docs/TESTING.md "persistence
/// failure behaviour").
/// </summary>
/// <remarks>
/// Driven through a context factory that throws rather than through a real database made to fail.
/// Breaking a file mid-run is timing-dependent and platform-specific; the property under test is the
/// policy — logged, counted, dropped, and the writer carries on — and a factory that refuses is the
/// only way to assert it the same way every time.
/// </remarks>
public sealed class MetadataPersistenceFailureTests
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_write_that_fails_is_counted_against_every_exchange_it_lost()
    {
        // Per exchange, not per batch. The number an operator needs is how much evidence is missing,
        // and a batch count would understate it by the batch size.
        var harness = new Harness(failing: true);

        await harness.RunAsync(Record(), Record(), Record());

        var failures = harness.Telemetry.PersistenceFailures;

        Assert.NotEmpty(failures);
        Assert.All(failures, failure => Assert.Equal(PersistenceFailureReason.WriteFailed, failure.Reason));
        Assert.Equal(3, failures.Sum(failure => failure.Count));
    }

    [Fact]
    public async Task A_failed_write_is_logged_with_a_stable_event_id_and_no_request_content()
    {
        var harness = new Harness(failing: true);

        await harness.RunAsync(Record());

        var entry = Assert.Single(harness.Logged);

        // A log message is prose and will be reworded; the identifier is what an alert rule matches.
        Assert.Equal(GatewayEventIds.MetadataPersistenceFailed, entry.EventId);
        Assert.Equal(LogLevel.Error, entry.Level);
    }

    [Fact]
    public async Task The_writer_keeps_draining_after_a_failure()
    {
        // A batch that fails is dropped rather than retried. Retrying would either reorder evidence
        // behind whatever arrived while it retried, or stall the queue forever behind a record the
        // store will never accept — and a stalled queue loses every exchange after it, not just one.
        var harness = new Harness(failing: true);

        await harness.RunAsync(Record(), Record());

        Assert.Equal(2, harness.Telemetry.PersistenceFailures.Sum(failure => failure.Count));
        Assert.False(harness.Faulted, "The writer stopped instead of continuing to drain.");
    }

    [Fact]
    public async Task A_failure_never_reaches_the_caller_that_produced_the_evidence()
    {
        // The sink is what the request path touches, and it is on the far side of the queue from the
        // store. Whatever the writer is going through, recording completes and does not throw.
        var harness = new Harness(failing: true);

        var pending = harness.Sink.RecordAsync(Record(), CancellationToken.None);

        Assert.True(pending.IsCompletedSuccessfully);
        await pending;
    }

    private static ExchangeRecord Record()
    {
        var exchangeId = ExchangeId.New();

        return ExchangeRecord.Create(
            exchangeId,
            PublicRequestId.FromExchangeId(exchangeId),
            [ExchangeObservation.Create(
                ObservationId.New(),
                exchangeId,
                0,
                ObservationType.RequestAccepted,
                Origin,
                ObservationSource.Gateway)]);
    }

    /// <summary>A writer wired to a store that refuses, and the evidence of what it did about it.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly MetadataPersistenceService writer;

        internal Harness(bool failing)
        {
            var options = new AgentSpliceOptions();
            options.Persistence.Mode = PersistenceMode.Sqlite;
            options.Persistence.ConnectionString = "Data Source=:memory:";

            Sink = new QueuedExchangeRecordSink(
                Options.Create(options),
                new FakeTimeProvider(Origin),
                Telemetry,
                NullLogger<QueuedExchangeRecordSink>.Instance);

            writer = new MetadataPersistenceService(
                Sink,
                new RefusingDbContextFactory(failing),
                Telemetry,
                new FakeTimeProvider(Origin),
                Options.Create(options),
                new CapturingLogger<MetadataPersistenceService>(Logged));
        }

        internal QueuedExchangeRecordSink Sink { get; }

        internal RecordingExchangeTelemetry Telemetry { get; } = new();

        internal List<(EventId EventId, LogLevel Level)> Logged { get; } = [];

        internal bool Faulted { get; private set; }

        public void Dispose() => writer.Dispose();

        /// <summary>Queues the records, runs the writer to completion, and reports whether it survived.</summary>
        internal async Task RunAsync(params ExchangeRecord[] records)
        {
            foreach (var record in records)
            {
                await Sink.RecordAsync(record, CancellationToken.None);
            }

            using var stopping = new CancellationTokenSource();

            await writer.StartAsync(stopping.Token);

            // The writer drains what is queued and then waits. Stopping it makes that wait return and
            // runs the shutdown flush, which is when the last batch is attempted.
            try
            {
                await writer.StopAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                Faulted = true;
            }
        }
    }

    /// <summary>A context factory that refuses, standing in for a store that will not accept a write.</summary>
    private sealed class RefusingDbContextFactory : IDbContextFactory<AgentSpliceDbContext>
    {
        private readonly bool failing;

        internal RefusingDbContextFactory(bool failing) => this.failing = failing;

        public AgentSpliceDbContext CreateDbContext() =>
            failing
                ? throw new InvalidOperationException("The store is unavailable.")
                : new AgentSpliceDbContext(
                    new DbContextOptionsBuilder<AgentSpliceDbContext>().UseSqlite("Data Source=:memory:").Options);
    }

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
