using AgentSplice.Application.Configuration;
using AgentSplice.Infrastructure.Persistence;
using AgentSplice.IntegrationTests.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentSplice.IntegrationTests.Persistence;

/// <summary>
/// A store the gateway cannot bring up to date fails startup with a reason, rather than holding it
/// forever (NFR 14.2, ADR 0024).
/// </summary>
/// <remarks>
/// Against a real SQLite file, because the lock is Entity Framework Core's and lives in the store: a
/// row it inserts before it migrates and deletes after, and waits on for as long as it finds one. A
/// gateway killed while it held the lock is stood in for by inserting that row, which is all a kill
/// leaves behind.
///
/// The lock is retried on the real clock and the bound is on the injected one, so the test decides
/// when the bound has passed, and a store that is not locked must start with the clock never moved.
/// </remarks>
public sealed class MigrationLockTests
{
    [Fact]
    public async Task A_lock_left_by_a_killed_gateway_fails_startup_and_names_the_lock()
    {
        using var store = new TemporaryMetadataStore();
        var time = new FakeTimeProvider();

        // Creates the store, and with it Entity Framework Core's lock table, and releases the lock.
        await Initializer(store, time).StartAsync(CancellationToken.None);
        await LeaveLockAsync(store);

        var starting = Initializer(store, time).StartAsync(CancellationToken.None);

        Assert.False(starting.IsCompleted, "Startup finished although the store was locked.");

        time.Advance(MetadataStoreInitializer.MigrationTimeout);

        // Bounded here too, so that the defect this guards against fails the test instead of hanging it.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => starting.WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Contains("__EFMigrationsLock", error.Message, StringComparison.Ordinal);
        Assert.Contains(new SqliteConnectionStringBuilder(store.ConnectionString).DataSource, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_start_stopped_while_it_waits_for_the_lock_is_not_reported_as_the_lock()
    {
        // The lock error tells the operator to delete the lock, which is advice for a lock nothing will
        // release. A start the host cancelled while it waited, such as Ctrl+C, is not that: the lock may
        // belong to a gateway that is migrating the same store right now. So it ends as the
        // cancellation it is.
        using var store = new TemporaryMetadataStore();
        var time = new FakeTimeProvider();

        await Initializer(store, time).StartAsync(CancellationToken.None);
        await LeaveLockAsync(store);

        using var stopping = new CancellationTokenSource();
        var starting = Initializer(store, time).StartAsync(stopping.Token);

        Assert.False(starting.IsCompleted, "Startup finished although the store was locked.");

        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task A_store_nothing_has_locked_starts_without_waiting_for_the_bound()
    {
        // The control for the test above: the same steps without the row, and the clock never moved.
        using var store = new TemporaryMetadataStore();
        var time = new FakeTimeProvider();

        await Initializer(store, time).StartAsync(CancellationToken.None);
        await Initializer(store, time).StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static MetadataStoreInitializer Initializer(TemporaryMetadataStore store, TimeProvider time)
    {
        var options = new AgentSpliceOptions();
        options.Persistence.Mode = PersistenceMode.Sqlite;
        options.Persistence.ConnectionString = store.ConnectionString;

        return new MetadataStoreInitializer(
            store.ContextFactory(),
            Options.Create(options),
            time,
            NullLogger<MetadataStoreInitializer>.Instance);
    }

    /// <summary>What a gateway killed while migrating leaves: the row Entity Framework Core deletes after.</summary>
    /// <remarks>
    /// Written against the table by name, so that Entity Framework Core moving its lock elsewhere fails
    /// this test rather than letting it pass against a table nothing reads.
    /// </remarks>
    private static async Task LeaveLockAsync(TemporaryMetadataStore store)
    {
        using var context = store.OpenContext();

        await context.Database.ExecuteSqlRawAsync(
            """INSERT INTO "__EFMigrationsLock" ("Id", "Timestamp") VALUES (1, '2026-08-01 09:00:00+00:00');""");
    }
}
