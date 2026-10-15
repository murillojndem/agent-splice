using AgentSplice.Application.Configuration;
using AgentSplice.Application.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSplice.Infrastructure.Persistence;

/// <summary>
/// Brings the metadata store's schema up to date before anything reads or writes it.
/// </summary>
/// <remarks>
/// Registered ahead of the writer and completing its work in <c>StartAsync</c>, so that neither the
/// writer nor an administrative query can reach a database whose tables do not exist yet.
///
/// A failure here fails startup, and that is deliberately different from how a failed write is
/// treated. A store that cannot be opened or migrated at all is a deployment fault — a bad path, a
/// read-only volume, a schema from a newer build — and NFR 14.2 puts that class of problem before
/// readiness rather than after it. A write that fails later is a runtime condition the gateway must
/// survive, which is why <see cref="MetadataPersistenceService"/> logs and continues instead
/// (FR-DATA-009). Silently starting with a broken store would produce a gateway that proxies
/// perfectly and retains nothing, which is the one failure an evidence product must not have
/// quietly.
///
/// Once the store is open it says where it is (<c>1305 MetadataStoreOpened</c>): a relative path is
/// resolved against the content root (ADR 0017), and an operator looking for their evidence should
/// not have to work out which directory that was. A store that cannot be opened at all fails with an
/// error naming the file, because SQLite's own message for a missing directory names nothing.
/// Neither ever carries the connection string, which may hold a password.
/// </remarks>
public sealed class MetadataStoreInitializer : IHostedService
{
    // SQLITE_CANTOPEN and SQLITE_READONLY: the store's file or directory is missing or not writable.
    private const int CantOpen = 14;
    private const int ReadOnly = 8;

    private readonly IDbContextFactory<AgentSpliceDbContext> contextFactory;
    private readonly IOptions<AgentSpliceOptions> options;
    private readonly ILogger<MetadataStoreInitializer> logger;

    /// <summary>Creates the initializer.</summary>
    public MetadataStoreInitializer(
        IDbContextFactory<AgentSpliceDbContext> contextFactory,
        IOptions<AgentSpliceOptions> options,
        ILogger<MetadataStoreInitializer> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        this.contextFactory = contextFactory;
        this.options = options;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Nothing to bring up to date, and nothing to create. A deployment that asked for no store
        // must not find a database file waiting for it (FR-DATA-001).
        if (!PersistenceRegistration.Retains(options.Value))
        {
            return;
        }

        await using var context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        // The connection string as the context was configured with it, so after resolution against
        // the content root — the file actually opened, not the setting as written.
        var store = SqliteStoreLocation.DescribeStore(context.Database.GetConnectionString() ?? string.Empty);

        try
        {
            // Migrations rather than EnsureCreated: the schema has to be versioned for an existing
            // store to survive an upgrade, and EnsureCreated leaves no way to alter one that already
            // exists.
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is CantOpen or ReadOnly)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"The metadata store '{store}' could not be opened for writing (SQLite error {exception.SqliteErrorCode}). Its directory must exist and be writable by this process. Point agentsplice:persistence:connectionString at another file, or set agentsplice:persistence:mode to None to retain nothing."),
                exception);
        }

        logger.LogInformation(
            GatewayEventIds.MetadataStoreOpened,
            "Metadata is stored in {StorePath}.",
            store);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
