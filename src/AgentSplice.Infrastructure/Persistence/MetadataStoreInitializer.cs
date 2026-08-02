using AgentSplice.Application.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
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
/// </remarks>
public sealed class MetadataStoreInitializer : IHostedService
{
    private readonly IDbContextFactory<AgentSpliceDbContext> contextFactory;
    private readonly IOptions<AgentSpliceOptions> options;

    /// <summary>Creates the initializer.</summary>
    public MetadataStoreInitializer(
        IDbContextFactory<AgentSpliceDbContext> contextFactory,
        IOptions<AgentSpliceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(options);

        this.contextFactory = contextFactory;
        this.options = options;
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

        // Migrations rather than EnsureCreated: the schema has to be versioned for an existing store
        // to survive an upgrade, and EnsureCreated leaves no way to alter one that already exists.
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
