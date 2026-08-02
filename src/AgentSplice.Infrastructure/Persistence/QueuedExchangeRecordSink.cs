using System.Threading.Channels;
using AgentSplice.Application.Configuration;
using AgentSplice.Application.Diagnostics;
using AgentSplice.Application.Exchanges;
using AgentSplice.Application.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSplice.Infrastructure.Persistence;

/// <summary>
/// Hands a finished request's evidence to the metadata writer without ever making the request path
/// wait for a database (FR-DATA-009, NFR-PERF-004).
/// </summary>
/// <remarks>
/// <see cref="RecordAsync"/> completes synchronously. It writes to a bounded channel with
/// <see cref="ChannelWriter{T}.TryWrite"/> and returns; it never awaits, never blocks, and never
/// throws. The gateway calls this from <c>FinishAsync</c>, which for a streamed exchange runs after
/// the client response is already complete — but for a buffered one runs before the body is written,
/// so any delay here would become client-visible latency for a request that had already succeeded.
///
/// The queue is bounded and full means drop. The alternatives are worse in the way that matters:
/// waiting converts a slow store into gateway latency and, eventually, into a stalled stream, while
/// growing without limit converts it into an out-of-memory kill that takes the proxy down with it.
/// A dropped record is one exchange missing from the store, which has to be visible as a counter
/// increment and a log line rather than as silence.
///
/// That last requirement is why the channel is created with <see cref="BoundedChannelFullMode.Wait"/>
/// and then never awaited. The mode names what <c>WriteAsync</c> would do; what
/// <see cref="ChannelWriter{T}.TryWrite"/> does under it is return <c>false</c> immediately, which is
/// the only mode that both refuses to block and reports the refusal. The two dropping modes look
/// closer to the intent and are not: under either of them <c>TryWrite</c> returns <c>true</c> having
/// silently discarded a record — <c>DropWrite</c> the one just handed over, <c>DropOldest</c> one that
/// had nearly survived — and evidence would go missing with nothing counting it.
/// </remarks>
public sealed class QueuedExchangeRecordSink : IExchangeRecordSink
{
    private readonly Channel<QueuedExchangeRecord> channel;
    private readonly TimeProvider timeProvider;
    private readonly IExchangeTelemetry telemetry;
    private readonly ILogger<QueuedExchangeRecordSink> logger;

    /// <summary>Creates the sink and its bounded queue.</summary>
    public QueuedExchangeRecordSink(
        IOptions<AgentSpliceOptions> options,
        TimeProvider timeProvider,
        IExchangeTelemetry telemetry,
        ILogger<QueuedExchangeRecordSink> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(logger);

        this.timeProvider = timeProvider;
        this.telemetry = telemetry;
        this.logger = logger;

        channel = Channel.CreateBounded<QueuedExchangeRecord>(
            new BoundedChannelOptions(options.Value.Persistence.MetadataQueueCapacity)
            {
                // See the remarks: this is the mode under which TryWrite refuses rather than
                // silently discards. Nothing here ever calls WriteAsync, so nothing ever waits.
                FullMode = BoundedChannelFullMode.Wait,

                // Many request threads produce; one background writer consumes.
                SingleReader = true,
                SingleWriter = false,
            });
    }

    /// <summary>The queue the metadata writer drains.</summary>
    internal ChannelReader<QueuedExchangeRecord> Reader => channel.Reader;

    /// <inheritdoc />
    public ValueTask RecordAsync(ExchangeRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Deliberately ignores the token. A sink must never fail an exchange, and the evidence for a
        // cancelled request is exactly the evidence worth keeping.
        if (channel.Writer.TryWrite(new QueuedExchangeRecord(record, timeProvider.GetUtcNow())))
        {
            return ValueTask.CompletedTask;
        }

        telemetry.RecordPersistenceFailure(PersistenceFailureReason.QueueSaturated);

        // The identifier and nothing else. A saturated queue is an operational fact about the
        // gateway, not an invitation to log what the request contained.
        logger.LogWarning(
            GatewayEventIds.MetadataQueueSaturated,
            "The metadata queue is full; evidence for request {RequestId} was dropped. The client response is unaffected.",
            record.RequestId.Value);

        return ValueTask.CompletedTask;
    }
}
