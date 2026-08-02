using AgentSplice.Application.Administration;
using AgentSplice.Application.Configuration;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Measurements;
using AgentSplice.Domain.Observations;
using AgentSplice.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentSplice.Infrastructure.Persistence;

/// <summary>
/// Reads retained evidence out of the metadata store.
/// </summary>
/// <remarks>
/// Every query is read-only and no-tracking: nothing on this surface writes, and tracking entities
/// that will never be saved costs memory per request for no purpose.
///
/// The projections map rows to views rather than exposing rows, so a column rename is not a wire
/// change and the store's shape is never the API's shape.
/// </remarks>
internal sealed class ExchangeQueryStore : IExchangeQueryStore
{
    private readonly IDbContextFactory<AgentSpliceDbContext> contextFactory;
    private readonly IOptions<AgentSpliceOptions> options;

    public ExchangeQueryStore(
        IDbContextFactory<AgentSpliceDbContext> contextFactory,
        IOptions<AgentSpliceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(options);

        this.contextFactory = contextFactory;
        this.options = options;
    }

    /// <summary>Whether this deployment has a store to read at all (FR-DATA-001).</summary>
    public bool Retains => PersistenceRegistration.Retains(options.Value);

    /// <inheritdoc />
    public async Task<ExchangePageView> ListAsync(ExchangeQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = context.Exchanges.AsNoTracking();

        if (query.Status is { } status)
        {
            rows = rows.Where(row => row.Status == (int)status);
        }

        if (query.Runtime is { } runtime)
        {
            rows = rows.Where(row => row.RuntimeEndpointId == runtime.Value);
        }

        if (query.After is { } after)
        {
            // The whole sort key, so a row written or expired between two pages cannot make the
            // second page skip or repeat one (FR-TRACE-009).
            rows = rows.Where(row =>
                row.StartedAtTicks < after.StartedAtTicks
                || (row.StartedAtTicks == after.StartedAtTicks && row.ExchangeId.CompareTo(after.ExchangeId) < 0));
        }

        // One more than asked for, which is how the next cursor is known to exist without a second
        // query and without reporting a cursor that leads to an empty page.
        var page = await rows
            .OrderByDescending(row => row.StartedAtTicks)
            .ThenByDescending(row => row.ExchangeId)
            .Take(query.Limit + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var hasMore = page.Count > query.Limit;
        var items = hasMore ? page.Take(query.Limit).ToList() : page;

        return new ExchangePageView
        {
            Items = items.Select(Summarise).ToList(),
            NextCursor = hasMore && items.Count > 0
                ? new ExchangeCursor(items[^1].StartedAtTicks, items[^1].ExchangeId).Encode()
                : null,
        };
    }

    /// <inheritdoc />
    public async Task<ExchangeDetailView?> FindAsync(ExchangeId exchangeId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var row = await context.Exchanges
            .AsNoTracking()
            .Include(exchange => exchange.Measurements)
            .FirstOrDefaultAsync(exchange => exchange.ExchangeId == exchangeId.Value, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        return new ExchangeDetailView
        {
            Summary = Summarise(row),
            IngressProtocol = (IngressProtocol)row.IngressProtocol,
            StreamTermination = (StreamTermination)row.StreamTermination,
            FailureClass = row.FailureClass is { } failure ? (FailureClass)failure : null,
            ErrorCode = row.ErrorCode,
            UpstreamStatusCode = row.UpstreamStatusCode,
            Measurements = row.Measurements
                .OrderBy(measurement => measurement.Name, StringComparer.Ordinal)
                .Select(Project)
                .ToList(),
            RequestSummaryJson = row.RequestSummaryJson,
            ResponseSummaryJson = row.ResponseSummaryJson,
            UsageJson = row.UsageJson,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TimelineObservationView>?> FindObservationsAsync(
        ExchangeId exchangeId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        // Existence is asked separately, so an exchange whose timeline is empty answers 200 with an
        // empty list while an exchange that is not there answers 404.
        var exists = await context.Exchanges
            .AsNoTracking()
            .AnyAsync(exchange => exchange.ExchangeId == exchangeId.Value, cancellationToken)
            .ConfigureAwait(false);

        if (!exists)
        {
            return null;
        }

        var rows = await context.Observations
            .AsNoTracking()
            .Where(observation => observation.ExchangeId == exchangeId.Value)
            .OrderBy(observation => observation.Sequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(Project).ToList();
    }

    private static ExchangeSummaryView Summarise(ExchangeRow row) => new()
    {
        ExchangeId = ExchangeId.From(row.ExchangeId),
        RequestId = row.PublicRequestId,
        TraceId = row.TraceId,
        StartedAt = new DateTimeOffset(row.StartedAtTicks, TimeSpan.Zero),
        CompletedAt = row.CompletedAtTicks is { } ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null,
        Status = (ExchangeStatus)row.Status,
        RuntimeId = row.RuntimeEndpointId,
        ClientModelId = row.ClientModelId,
        UpstreamModelId = row.UpstreamModelId,
        Streaming = row.Streaming,
        ContentRetentionState = (ContentRetentionState)row.ContentRetentionState,
    };

    private static MeasurementView Project(ExchangeMeasurementRow row) => new()
    {
        Name = row.Name,
        Value = row.Value,
        Unit = (MeasurementUnit)row.Unit,
        Provenance = (MeasurementProvenance)row.Provenance,
        Confidence = row.Confidence,
    };

    private static TimelineObservationView Project(ExchangeObservationRow row) => new()
    {
        Sequence = row.Sequence,
        Type = (ObservationType)row.Type,
        Timestamp = new DateTimeOffset(row.TimestampTicks, TimeSpan.Zero),
        Source = (ObservationSource)row.Source,
        Confidence = row.Confidence,
        DetailsJson = row.DetailsJson,
    };
}
