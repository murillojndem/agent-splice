using AgentSplice.Application.Configuration;
using AgentSplice.Application.Exchanges;
using AgentSplice.Application.Observability;
using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Observations;
using AgentSplice.Infrastructure.Persistence;
using AgentSplice.UnitTests.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentSplice.UnitTests.Persistence;

/// <summary>
/// The sink's contract with the request path: it takes evidence and gets out of the way.
/// </summary>
/// <remarks>
/// The gateway calls this from <c>FinishAsync</c>, which for a buffered exchange runs before the
/// response body is written. Anything that blocks, awaits, or throws here becomes latency or a
/// failure on a request that had already succeeded (FR-DATA-009).
/// </remarks>
public sealed class QueuedExchangeRecordSinkTests
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Recording_completes_without_waiting_for_a_writer()
    {
        // Nothing is draining the queue. If RecordAsync waited for capacity, or for a write, this
        // would never return.
        var sink = Sink(capacity: 4, out _, out _);

        var pending = sink.RecordAsync(Record(), CancellationToken.None);

        Assert.True(pending.IsCompletedSuccessfully);
        await pending;
    }

    [Fact]
    public async Task A_full_queue_drops_the_record_and_counts_it_rather_than_blocking()
    {
        // Waiting would convert a slow store into gateway latency and eventually into a stalled
        // stream; growing without limit would convert it into an out-of-memory kill. Dropping is the
        // only option that keeps the proxy answering, and it has to be visible when it happens.
        //
        // This test failed first against BoundedChannelFullMode.DropWrite, which reads like the
        // intent and is not: TryWrite returns true having discarded the record, so every drop was
        // silent and the counter never moved.
        var sink = Sink(capacity: 2, out var telemetry, out _);

        for (var index = 0; index < 5; index++)
        {
            await sink.RecordAsync(Record(), CancellationToken.None);
        }

        Assert.Equal(3, telemetry.PersistenceFailures.Count);
        Assert.All(
            telemetry.PersistenceFailures,
            failure => Assert.Equal(PersistenceFailureReason.QueueSaturated, failure.Reason));
    }

    [Fact]
    public async Task A_full_queue_keeps_the_records_it_already_holds()
    {
        // Refusing the new record, never evicting an old one. Under sustained saturation the oldest
        // records are the ones about to be written, so DropOldest would lose evidence that had nearly
        // survived in favour of evidence that has not yet queued.
        var sink = Sink(capacity: 1, out _, out _);
        var first = Record();

        await sink.RecordAsync(first, CancellationToken.None);
        await sink.RecordAsync(Record(), CancellationToken.None);

        Assert.True(sink.Reader.TryRead(out var queued));
        Assert.Equal(first.ExchangeId, queued!.Record.ExchangeId);
    }

    [Fact]
    public async Task The_queued_boundary_is_stamped_when_the_record_entered_the_queue()
    {
        // Not when the writer picks it up. The two differ by however long the store was busy, and
        // that interval is the entire reason MetadataQueued and PersistenceCompleted are separate
        // boundaries (ADR 0010).
        var sink = Sink(capacity: 4, out _, out var clock);

        await sink.RecordAsync(Record(), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.True(sink.Reader.TryRead(out var queued));
        Assert.Equal(Origin, queued!.QueuedAt);
    }

    [Fact]
    public async Task A_cancelled_request_is_still_recorded()
    {
        // The evidence for a cancelled exchange is exactly the evidence worth keeping, so the sink
        // ignores the token it is handed rather than honouring it.
        var sink = Sink(capacity: 4, out _, out _);

        await sink.RecordAsync(Record(), new CancellationToken(canceled: true));

        Assert.True(sink.Reader.TryRead(out _));
    }

    private static QueuedExchangeRecordSink Sink(
        int capacity,
        out RecordingExchangeTelemetry telemetry,
        out FakeTimeProvider clock)
    {
        telemetry = new RecordingExchangeTelemetry();
        clock = new FakeTimeProvider(Origin);

        var options = new AgentSpliceOptions();
        options.Persistence.MetadataQueueCapacity = capacity;

        return new QueuedExchangeRecordSink(
            Options.Create(options),
            clock,
            telemetry,
            NullLogger<QueuedExchangeRecordSink>.Instance);
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
}
