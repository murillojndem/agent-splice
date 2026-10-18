using System.Diagnostics;
using AgentSplice.Application.Administration;
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

        // Counted for the system endpoint too, which is what an operator sees with export off.
        Assert.Equal(3, harness.Failures.WriteFailed);
        Assert.Equal(0, harness.Failures.QueueSaturated);
    }

    [Fact]
    public async Task A_failed_write_is_logged_with_a_stable_event_id()
    {
        // Named for what it shows. A record holds no request content to leak, so "and no request
        // content" claimed something this test cannot see; LogRedactionTests searches the real host's
        // logs for content instead.
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
        // One record more than a batch holds, so a second batch comes after the refused one.
        var harness = new Harness(failing: true);

        await harness.RunAsync(Records(MetadataPersistenceService.MaxBatchSize + 1));

        Assert.Equal(
            [MetadataPersistenceService.MaxBatchSize, 1],
            harness.Telemetry.PersistenceFailures.Select(failure => failure.Count));
        Assert.False(harness.Faulted, "The writer stopped instead of continuing to drain.");
    }

    [Fact]
    public async Task The_queue_is_flushed_even_when_the_writer_is_stopped_before_it_started()
    {
        // Since .NET 10 a BackgroundService starts ExecuteAsync through Task.Run with its stopping
        // token, so a stop that wins the race cancels the loop before its first line, and the
        // shutdown flush with it. A start token that is already cancelled loses that race every time,
        // which is what makes this deterministic. The refusing store is the observable: a counted
        // failure is a write the flush attempted (ADR 0024).
        var harness = new Harness(failing: true);

        await harness.RunStoppedBeforeStartAsync(CancellationToken.None, Record(), Record());

        Assert.Equal(2, harness.Telemetry.PersistenceFailures.Sum(failure => failure.Count));
        Assert.False(harness.Faulted, "Stopping the writer threw.");
    }

    [Fact]
    public async Task Stopping_a_writer_that_never_started_waits_no_longer_than_the_host_allows()
    {
        // That flush is bounded by the shutdown token, as the loop's is: the host's shutdown timeout
        // bounds how long stopping takes (ADR 0013, ADR 0024). The store blocks the thread that asks,
        // as SQLite's busy wait does for a locked store, until the test lets it answer or it gives up;
        // stopping returns at the token, and the writes go on behind it.
        var harness = new Harness(failing: true, answering: false);
        var clock = Stopwatch.StartNew();

        await harness.RunStoppedBeforeStartAsync(new CancellationToken(canceled: true), Record(), Record());

        clock.Stop();

        // A flush on the caller's thread would have held it until the store gave up.
        Assert.True(
            clock.Elapsed < RefusingDbContextFactory.GivesUpAfter / 2,
            FormattableString.Invariant($"Stopping held its caller for {clock.Elapsed.TotalMilliseconds:F0} ms while the store did not answer."));
        Assert.False(harness.Faulted, "Stopping the writer threw.");

        harness.Store.Answer();
        await harness.Store.AttemptsAsync(1);
    }

    [Fact]
    public async Task A_stop_that_times_out_while_the_loop_is_writing_starts_no_second_reader()
    {
        // One reader drains the queue, whichever of the loop and StopAsync claims it (ADR 0013,
        // ADR 0024). Here the loop holds its first batch at a store that is not answering, a record
        // is still queued, and the host's shutdown timeout has already passed: a second reader in
        // StopAsync would take that record to the store at once, beside the loop's batch.
        var harness = new Harness(failing: true, answering: false);

        await harness.StartAsync(Records(MetadataPersistenceService.MaxBatchSize + 1));
        await harness.Store.EnteredAsync();
        await harness.StopAsync(new CancellationToken(canceled: true));

        Assert.False(
            await harness.Store.EnteredAsync(TimeSpan.FromSeconds(1)),
            "A second reader took a batch to the store while the loop held one.");

        harness.Store.Answer();
        await harness.LoopFinishedAsync();

        Assert.Equal(1, harness.Store.MostAtOnce);
        Assert.Equal(MetadataPersistenceService.MaxBatchSize + 1, harness.Telemetry.PersistenceFailures.Sum(failure => failure.Count));
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

    [Fact]
    public void A_clock_that_stepped_backwards_produces_no_persistence_measurement()
    {
        // Zero is a measurement: it says the write completed instantaneously. Recording one with
        // provenance Measured, and an end preceding its own start, is evidence that contradicts
        // itself — so an impossibly ordered interval yields no row at all, exactly as it does in the
        // recorder's duration builder and the gateway's histogram guard (FR-TRACE-006, FR-OBS-004).
        var queuedAt = Origin;
        var completedAt = Origin.AddSeconds(-5);

        Assert.Null(MetadataPersistenceService.PersistenceDurationFor(Queued(queuedAt), completedAt));

        // The ordinary direction still produces one, so the guard is not simply switching it off.
        var measured = MetadataPersistenceService.PersistenceDurationFor(
            Queued(queuedAt),
            Origin.AddMilliseconds(40));

        Assert.NotNull(measured);
        Assert.Equal(40d, measured.Value, precision: 3);
    }

    private static QueuedExchangeRecord Queued(DateTimeOffset queuedAt) => new(Record(), queuedAt);

    private static ExchangeRecord[] Records(int count) =>
        Enumerable.Range(0, count).Select(_ => Record()).ToArray();

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

        internal Harness(bool failing, bool answering = true)
        {
            var options = new AgentSpliceOptions();
            options.Persistence.Mode = PersistenceMode.Sqlite;
            options.Persistence.ConnectionString = "Data Source=:memory:";

            Sink = new QueuedExchangeRecordSink(
                Options.Create(options),
                new FakeTimeProvider(Origin),
                Telemetry,
                Failures,
                NullLogger<QueuedExchangeRecordSink>.Instance);

            Store = new RefusingDbContextFactory(failing, answering);

            writer = new MetadataPersistenceService(
                Sink,
                Store,
                Telemetry,
                Failures,
                new FakeTimeProvider(Origin),
                Options.Create(options),
                new CapturingLogger<MetadataPersistenceService>(Logged));
        }

        internal QueuedExchangeRecordSink Sink { get; }

        internal RefusingDbContextFactory Store { get; }

        internal RecordingExchangeTelemetry Telemetry { get; } = new();

        internal PersistenceFailureCounts Failures { get; } = new();

        internal List<(EventId EventId, LogLevel Level)> Logged { get; } = [];

        internal bool Faulted { get; private set; }

        public void Dispose()
        {
            writer.Dispose();
            Store.Dispose();
        }

        /// <summary>
        /// Queues the records, lets the writer's loop attempt every one, stops it, and reports whether
        /// it survived.
        /// </summary>
        internal async Task RunAsync(params ExchangeRecord[] records)
        {
            await StartAsync(records);

            // Each batch attempted before the stop, so what the tests assert is the loop's doing and
            // not the shutdown flush's. Since .NET 10 the loop starts on the thread pool, and a stop
            // straight after StartAsync would usually reach a writer whose loop had not run, which is
            // the path the flush in StopAsync covers. One refused context per batch.
            var batches = (records.Length + MetadataPersistenceService.MaxBatchSize - 1) / MetadataPersistenceService.MaxBatchSize;
            await Store.AttemptsAsync(batches);

            await StopAsync(CancellationToken.None);
        }

        /// <summary>
        /// Queues the records and stops, at <paramref name="shutdown"/>, a writer whose loop was cancelled
        /// before it ran.
        /// </summary>
        internal async Task RunStoppedBeforeStartAsync(CancellationToken shutdown, params ExchangeRecord[] records)
        {
            await QueueAsync(records);

            using var starting = new CancellationTokenSource();
            await starting.CancelAsync();

            await writer.StartAsync(starting.Token);
            await StopAsync(shutdown);
        }

        /// <summary>Queues the records and starts the writer.</summary>
        internal async Task StartAsync(params ExchangeRecord[] records)
        {
            await QueueAsync(records);
            await writer.StartAsync(CancellationToken.None);
        }

        /// <summary>Stops the writer at <paramref name="shutdown"/>, noting whether stopping threw.</summary>
        internal async Task StopAsync(CancellationToken shutdown)
        {
            try
            {
                await writer.StopAsync(shutdown);
            }
            catch (Exception)
            {
                Faulted = true;
            }
        }

        /// <summary>Completes once the writer's loop has returned, its flush included.</summary>
        internal Task LoopFinishedAsync() => writer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        private async Task QueueAsync(ExchangeRecord[] records)
        {
            foreach (var record in records)
            {
                await Sink.RecordAsync(record, CancellationToken.None);
            }
        }
    }

    /// <summary>A context factory that refuses, standing in for a store that will not accept a write.</summary>
    /// <remarks>
    /// A store that is not answering holds the thread that asks until it is told to answer, or until it
    /// gives up, which is how Microsoft.Data.Sqlite waits for a locked store: its asynchronous calls
    /// complete synchronously, and its busy wait sleeps. So the writer is left to the interface's own
    /// <c>CreateDbContextAsync</c>, which calls <see cref="CreateDbContext"/> on the asking thread. Every
    /// request is counted as it arrives and as it ends, and so is how many the store held at once, so a
    /// test can wait for what it needs rather than for time to pass.
    /// </remarks>
    private sealed class RefusingDbContextFactory : IDbContextFactory<AgentSpliceDbContext>, IDisposable
    {
        /// <summary>How long a store that is not answering holds a request before refusing it anyway.</summary>
        internal static readonly TimeSpan GivesUpAfter = TimeSpan.FromSeconds(5);

        private readonly bool failing;
        private readonly ManualResetEventSlim answering;
        private readonly SemaphoreSlim entered = new(0);
        private readonly SemaphoreSlim attempts = new(0);
        private readonly object counting = new();
        private int inside;
        private int mostAtOnce;

        internal RefusingDbContextFactory(bool failing, bool answering)
        {
            this.failing = failing;
            this.answering = new ManualResetEventSlim(answering);
        }

        /// <summary>The most requests the store has held at once.</summary>
        internal int MostAtOnce
        {
            get
            {
                lock (counting)
                {
                    return mostAtOnce;
                }
            }
        }

        public AgentSpliceDbContext CreateDbContext()
        {
            lock (counting)
            {
                inside++;
                mostAtOnce = Math.Max(mostAtOnce, inside);
            }

            entered.Release();

            try
            {
                answering.Wait(GivesUpAfter);

                return failing
                    ? throw new InvalidOperationException("The store is unavailable.")
                    : new AgentSpliceDbContext(
                        new DbContextOptionsBuilder<AgentSpliceDbContext>().UseSqlite("Data Source=:memory:").Options);
            }
            finally
            {
                lock (counting)
                {
                    inside--;
                }

                attempts.Release();
            }
        }

        public void Dispose()
        {
            answering.Dispose();
            entered.Dispose();
            attempts.Dispose();
        }

        /// <summary>Lets every request the store is holding, and every later one, through.</summary>
        internal void Answer() => answering.Set();

        /// <summary>Completes once a request has reached the store.</summary>
        internal async Task EnteredAsync() =>
            Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(10)), "The writer never asked the store.");

        /// <summary>Whether another request reaches the store within <paramref name="wait"/>.</summary>
        internal Task<bool> EnteredAsync(TimeSpan wait) => entered.WaitAsync(wait);

        /// <summary>Completes once the writer has asked for <paramref name="count"/> contexts and been answered.</summary>
        internal async Task AttemptsAsync(int count)
        {
            for (var attempt = 0; attempt < count; attempt++)
            {
                // Generous: reached only when the writer has stopped asking.
                Assert.True(
                    await attempts.WaitAsync(TimeSpan.FromSeconds(10)),
                    "The writer did not attempt every batch it was given.");
            }
        }
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
