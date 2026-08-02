using AgentSplice.Application.Configuration;
using AgentSplice.Application.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSplice.Infrastructure.Persistence;

/// <summary>
/// Removes exchange metadata past its retention window (FR-DATA-007, FR-DATA-008).
/// </summary>
/// <remarks>
/// A retention window is a promise about what is <em>not</em> kept. Until something enforces it, the
/// setting describes an intention and the database grows forever, which is the failure mode a
/// local-first product notices last and cares about most.
///
/// Deletion is idempotent by construction: the sweep asks for rows older than a cutoff and deletes
/// them, so running it twice, or interrupting it halfway, leaves the same store either way. It is
/// auditable by emitting what it removed under a stable event ID — counts and the window, never the
/// identifiers of what was deleted, because a log that names deleted evidence is a copy of the
/// evidence that outlives the retention policy.
///
/// Batched for the same reason the writer is: a sweep that deleted a year of backlog in one
/// transaction would hold a write lock against the writer and the administrative reads for as long as
/// it took.
/// </remarks>
public sealed class RetentionSweepService : BackgroundService
{
    /// <summary>Largest number of exchanges removed in one transaction.</summary>
    internal const int MaxBatchSize = 500;

    private readonly IDbContextFactory<AgentSpliceDbContext> contextFactory;
    private readonly IOptions<AgentSpliceOptions> options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<RetentionSweepService> logger;

    /// <summary>Creates the sweep.</summary>
    public RetentionSweepService(
        IDbContextFactory<AgentSpliceDbContext> contextFactory,
        IOptions<AgentSpliceOptions> options,
        TimeProvider timeProvider,
        ILogger<RetentionSweepService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.contextFactory = contextFactory;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!PersistenceRegistration.Retains(options.Value))
        {
            return;
        }

        var interval = options.Value.Capture.Retention.SweepInterval;

        // Once at startup, before the first tick. A process restarted more often than its sweep
        // interval would otherwise never sweep at all — which is the ordinary case for a local
        // gateway someone starts with their editor.
        await SweepAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(interval, timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    /// <summary>Removes everything past the window, in batches, and reports what went.</summary>
    /// <remarks>
    /// A failure is logged and the sweep is left for the next tick. Retrying immediately against a
    /// store that just refused would turn a broken database into a busy loop, and unlike a dropped
    /// write there is nothing lost by waiting: the rows are still there to remove.
    /// </remarks>
    internal async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var window = options.Value.Capture.Retention.Metadata;
        var cutoff = timeProvider.GetUtcNow() - window;
        var removed = 0;

        try
        {
            await using var context = await contextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                // Identifiers first, then a delete keyed on them. Expressing the whole thing as one
                // bounded delete would depend on the provider translating a limit into DELETE, which
                // SQLite does not always support and PostgreSQL spells differently.
                var expired = await context.Exchanges
                    .Where(row => row.StartedAtTicks < cutoff.UtcTicks)
                    .OrderBy(row => row.StartedAtTicks)
                    .Select(row => row.ExchangeId)
                    .Take(MaxBatchSize)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (expired.Count == 0)
                {
                    break;
                }

                // Observations and measurements go with them through the cascade declared on the
                // relationships, so no batch can leave rows no API can reach and no policy will
                // expire (FR-DATA-008).
                removed += await context.Exchanges
                    .Where(row => expired.Contains(row.ExchangeId))
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                GatewayEventIds.RetentionSweepFailed,
                exception,
                "A retention sweep failed after removing {RemovedCount} exchanges. It will be retried on the next interval.",
                removed);

            return removed;
        }

        if (removed > 0)
        {
            // Counts and the window. Never which exchanges went: a log line naming deleted evidence
            // is a copy of that evidence, outliving the policy that removed it.
            logger.LogInformation(
                GatewayEventIds.RetentionSweepCompleted,
                "Retention removed {RemovedCount} exchanges older than {RetentionWindow}.",
                removed,
                window);
        }

        return removed;
    }
}
