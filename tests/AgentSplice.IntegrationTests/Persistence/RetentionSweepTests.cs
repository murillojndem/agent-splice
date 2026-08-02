using AgentSplice.Application.Configuration;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Observations;
using AgentSplice.Infrastructure.Persistence;
using AgentSplice.Infrastructure.Persistence.Rows;
using AgentSplice.IntegrationTests.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentSplice.IntegrationTests.Persistence;

/// <summary>
/// Retention removes what its window says it removes, and nothing else (FR-DATA-007, FR-DATA-008).
/// </summary>
/// <remarks>
/// Driven against a real SQLite file, because the property that matters most — that a deleted
/// exchange takes its observations and measurements with it — is enforced by the schema's cascade
/// rather than by any code here. A fake store would assert the intention and miss the mechanism.
/// </remarks>
public sealed class RetentionSweepTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Only_exchanges_past_the_window_are_removed()
    {
        using var store = new TemporaryMetadataStore();
        await InitialiseAsync(store);

        var expired = await SeedAsync(store, Now.AddDays(-31));
        var kept = await SeedAsync(store, Now.AddDays(-29));

        var sweep = Sweep(store, TimeSpan.FromDays(30));

        Assert.Equal(1, await sweep.SweepAsync(CancellationToken.None));

        using var context = store.OpenContext();
        var remaining = await context.Exchanges.Select(row => row.ExchangeId).ToListAsync();

        Assert.Equal([kept], remaining);
        Assert.DoesNotContain(expired, remaining);
    }

    [Fact]
    public async Task A_second_sweep_removes_nothing_and_says_so()
    {
        // Idempotent by construction: the sweep asks for rows older than a cutoff, so running it
        // twice or interrupting it halfway leaves the same store either way (FR-DATA-008).
        using var store = new TemporaryMetadataStore();
        await InitialiseAsync(store);
        await SeedAsync(store, Now.AddDays(-31));

        var sweep = Sweep(store, TimeSpan.FromDays(30));

        Assert.Equal(1, await sweep.SweepAsync(CancellationToken.None));
        Assert.Equal(0, await sweep.SweepAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_removed_exchange_takes_its_timeline_and_measurements_with_it()
    {
        // Otherwise retention leaves rows no API can reach and no policy will ever expire again.
        using var store = new TemporaryMetadataStore();
        await InitialiseAsync(store);
        await SeedAsync(store, Now.AddDays(-31));

        using (var before = store.OpenContext())
        {
            Assert.NotEmpty(await before.Observations.ToListAsync());
            Assert.NotEmpty(await before.Measurements.ToListAsync());
        }

        await Sweep(store, TimeSpan.FromDays(30)).SweepAsync(CancellationToken.None);

        using var after = store.OpenContext();

        Assert.Empty(await after.Exchanges.ToListAsync());
        Assert.Empty(await after.Observations.ToListAsync());
        Assert.Empty(await after.Measurements.ToListAsync());
    }

    [Fact]
    public async Task A_backlog_larger_than_one_batch_is_removed_completely()
    {
        // The batch bound exists so a long-neglected store is drained in steady increments rather
        // than in one transaction that holds a write lock against the writer.
        using var store = new TemporaryMetadataStore();
        await InitialiseAsync(store);

        var count = RetentionSweepService.MaxBatchSize + 7;

        for (var index = 0; index < count; index++)
        {
            await SeedAsync(store, Now.AddDays(-40));
        }

        Assert.Equal(count, await Sweep(store, TimeSpan.FromDays(30)).SweepAsync(CancellationToken.None));

        using var context = store.OpenContext();

        Assert.Empty(await context.Exchanges.ToListAsync());
    }

    private static RetentionSweepService Sweep(TemporaryMetadataStore store, TimeSpan window)
    {
        var options = new AgentSpliceOptions();
        options.Persistence.Mode = PersistenceMode.Sqlite;
        options.Persistence.ConnectionString = store.ConnectionString;
        options.Capture.Retention.Metadata = window;

        return new RetentionSweepService(
            store.ContextFactory(),
            Options.Create(options),
            new FakeTimeProvider(Now),
            NullLogger<RetentionSweepService>.Instance);
    }

    private static async Task InitialiseAsync(TemporaryMetadataStore store)
    {
        using var context = store.OpenContext();
        await context.Database.MigrateAsync();
    }

    /// <summary>Writes one exchange with a timeline and a measurement, started at a chosen moment.</summary>
    private static async Task<Guid> SeedAsync(TemporaryMetadataStore store, DateTimeOffset startedAt)
    {
        var exchangeId = Guid.NewGuid();

        using var context = store.OpenContext();

        var row = new ExchangeRow
        {
            ExchangeId = exchangeId,
            PublicRequestId = exchangeId.ToString(),
            IngressProtocol = (int)IngressProtocol.OpenAiChatCompletions,
            StartedAtTicks = startedAt.UtcTicks,
            Status = (int)ExchangeStatus.Completed,
            StreamTermination = (int)StreamTermination.NotApplicable,
            ContentRetentionState = (int)ContentRetentionState.MetadataOnly,
        };

        row.Observations.Add(new ExchangeObservationRow
        {
            ObservationId = Guid.NewGuid(),
            ExchangeId = exchangeId,
            Sequence = 0,
            Type = (int)ObservationType.RequestAccepted,
            TimestampTicks = startedAt.UtcTicks,
            Source = (int)ObservationSource.Gateway,
        });

        row.Measurements.Add(new ExchangeMeasurementRow
        {
            MeasurementId = Guid.NewGuid(),
            ExchangeId = exchangeId,
            Name = "exchange.total.duration",
            Value = 1d,
            Unit = 1,
            Provenance = 1,
        });

        context.Exchanges.Add(row);
        await context.SaveChangesAsync();

        return exchangeId;
    }
}
