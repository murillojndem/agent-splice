using AgentSplice.Application.Configuration;
using AgentSplice.Application.Diagnostics;
using AgentSplice.Application.Observability;
using AgentSplice.Domain.Observations;
using AgentSplice.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSplice.Infrastructure.Persistence;

/// <summary>
/// Drains the metadata queue into the store.
/// </summary>
/// <remarks>
/// The only writer. Running outside the request path is what lets the store be slow, or briefly
/// unavailable, without any of it reaching a client (FR-DATA-009).
///
/// Records are written in batches with a short transaction each, never one long-lived transaction
/// over the whole queue: CLAUDE.md requires metadata persistence to complete outside long-lived
/// database transactions, and a batch that held a write lock while the queue refilled would block the
/// retention sweep and the administrative reads for as long as traffic continued.
///
/// A batch that fails is logged, counted, and dropped. Retrying it would either reorder evidence
/// behind the records that arrived while it was retrying, or stall the queue behind a record the
/// store will never accept — and the failure itself is recorded as a counter and a log line rather
/// than as an observation row, because the store that rejected the write is the one a
/// <see cref="ObservationType.PersistenceFailed"/> row would have to live in.
/// </remarks>
public sealed class MetadataPersistenceService : BackgroundService
{
    /// <summary>
    /// Largest number of exchanges written in one transaction.
    /// </summary>
    /// <remarks>
    /// Bounded so a backlog is drained in steady increments rather than in one transaction whose
    /// size is decided by however far behind the writer fell.
    /// </remarks>
    internal const int MaxBatchSize = 64;

    private readonly QueuedExchangeRecordSink sink;
    private readonly IDbContextFactory<AgentSpliceDbContext> contextFactory;
    private readonly IExchangeTelemetry telemetry;
    private readonly TimeProvider timeProvider;
    private readonly IOptions<AgentSpliceOptions> options;
    private readonly ILogger<MetadataPersistenceService> logger;

    /// <summary>Creates the writer.</summary>
    public MetadataPersistenceService(
        QueuedExchangeRecordSink sink,
        IDbContextFactory<AgentSpliceDbContext> contextFactory,
        IExchangeTelemetry telemetry,
        TimeProvider timeProvider,
        IOptions<AgentSpliceOptions> options,
        ILogger<MetadataPersistenceService> logger)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        this.sink = sink;
        this.contextFactory = contextFactory;
        this.telemetry = telemetry;
        this.timeProvider = timeProvider;
        this.options = options;
        this.logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // With no store the sink resolved is the discarding one, so nothing will ever reach this
        // queue. Standing down keeps the context factory untouched and no database file created.
        if (!PersistenceRegistration.Retains(options.Value))
        {
            return;
        }

        try
        {
            // The wait is cancellable; the write it leads to is not. Shutdown must stop the writer
            // from picking up more work, not abandon work it has already taken off the queue.
            while (await sink.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                await WriteBatchAsync(Dequeue()).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown, noticed while waiting for more work.
        }

        // The shutdown flush, and it belongs here rather than in StopAsync. Everything still queued
        // was accepted from a client, so stopping is not a reason to discard it — but a second reader
        // draining the same channel from StopAsync would race this loop whenever the host's shutdown
        // timeout elapsed before it finished. One reader, and StopAsync waits for it.
        while (sink.Reader.TryPeek(out _))
        {
            await WriteBatchAsync(Dequeue()).ConfigureAwait(false);
        }
    }

    private List<QueuedExchangeRecord> Dequeue()
    {
        var batch = new List<QueuedExchangeRecord>(MaxBatchSize);

        while (batch.Count < MaxBatchSize && sink.Reader.TryRead(out var queued))
        {
            batch.Add(queued);
        }

        return batch;
    }

    /// <summary>
    /// Writes one batch and then stamps its completion.
    /// </summary>
    /// <remarks>
    /// Two transactions, deliberately.
    /// <see cref="ObservationType.PersistenceCompleted"/> names a moment that does not exist until
    /// the first commit has returned, so including it in that commit would stamp it from before the
    /// operation it claims to describe — the defect class ADR 0010 was written to stop. The cost is
    /// one small insert per batch, off the request path; the failure mode is an exchange whose
    /// timeline ends at <see cref="ObservationType.MetadataQueued"/>, which reads as exactly what
    /// happened.
    ///
    /// Nothing here is cancellable, and that is the point. These records have already left the queue,
    /// so a token that aborted the write would discard them with nothing left holding them — and the
    /// token available at shutdown fires exactly when the queue is most likely to hold a backlog. The
    /// same rule the gateway applies when it hands a cancelled request's evidence to the sink under
    /// <see cref="CancellationToken.None"/>: the evidence for an interrupted operation is the evidence
    /// most worth keeping (FR-DATA-009).
    /// </remarks>
    private async Task WriteBatchAsync(List<QueuedExchangeRecord> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        using var span = telemetry.StartPersistence(batch.Count);

        try
        {
            await using var context = await contextFactory
                .CreateDbContextAsync(CancellationToken.None)
                .ConfigureAwait(false);

            foreach (var queued in batch)
            {
                var row = ExchangeRowMapper.ToRow(queued.Record);
                row.Observations.Add(Boundary(row, ObservationType.MetadataQueued, queued.QueuedAt));
                context.Exchanges.Add(row);
            }

            await context.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            await StampCompletionAsync(batch).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            telemetry.RecordPersistenceFailure(PersistenceFailureReason.WriteFailed, batch.Count);

            logger.LogError(
                GatewayEventIds.MetadataPersistenceFailed,
                exception,
                "Writing metadata for {ExchangeCount} exchanges failed. Client responses are unaffected.",
                batch.Count);
        }
    }

    /// <summary>Appends the completion boundary, stamped after the batch was durable.</summary>
    /// <remarks>
    /// Its own try/catch. A failure here means the evidence is stored and one boundary is missing,
    /// which is not the same as a lost batch and must not be counted as one.
    /// </remarks>
    private async Task StampCompletionAsync(List<QueuedExchangeRecord> batch)
    {
        var completedAt = timeProvider.GetUtcNow();

        try
        {
            await using var context = await contextFactory
                .CreateDbContextAsync(CancellationToken.None)
                .ConfigureAwait(false);

            foreach (var queued in batch)
            {
                context.Observations.Add(new ExchangeObservationRow
                {
                    ObservationId = Guid.NewGuid(),
                    ExchangeId = queued.Record.ExchangeId.Value,

                    // One past MetadataQueued, which the first transaction appended.
                    Sequence = queued.Record.Observations.Count + 1,
                    Type = (int)ObservationType.PersistenceCompleted,
                    TimestampTicks = completedAt.UtcTicks,
                    Source = (int)ObservationSource.Gateway,
                });
            }

            await context.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(
                GatewayEventIds.MetadataPersistenceFailed,
                exception,
                "Metadata for {ExchangeCount} exchanges was stored but its completion boundary was not.",
                batch.Count);
        }
    }

    private static ExchangeObservationRow Boundary(
        ExchangeRow row,
        ObservationType type,
        DateTimeOffset timestamp) =>
        new()
        {
            ObservationId = Guid.NewGuid(),
            ExchangeId = row.ExchangeId,
            Sequence = row.Observations.Count,
            Type = (int)type,
            TimestampTicks = timestamp.UtcTicks,
            Source = (int)ObservationSource.Gateway,
        };
}
