using AgentSplice.Application.Configuration;
using AgentSplice.Application.Exchanges;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentSplice.Infrastructure.Persistence;

/// <summary>
/// Registers the metadata store, or deliberately does not.
/// </summary>
/// <remarks>
/// FR-DATA-001 requires purely ephemeral operation to remain possible, so "no store" is a supported
/// deployment and not a degraded one. With a store the evidence goes to a queue and a background
/// writer; without one it goes to <see cref="NullExchangeRecordSink"/> and the gateway keeps proxying
/// with nothing retained.
///
/// The decision is made when a service is resolved, never while services are being registered.
/// Reading <c>IConfiguration</c> here would read it half-built: a host layers its sources as it is
/// assembled — a test host adds its overrides through <c>ConfigureAppConfiguration</c>, which runs
/// after the composition root has already run — so a registration-time read sees whatever happened to
/// be present at that instant and silently disagrees with the <c>IOptions</c> value every other part
/// of the system uses.
/// </remarks>
public static class PersistenceRegistration
{
    /// <summary>Registers metadata persistence according to <c>agentsplice:persistence</c>.</summary>
    public static IServiceCollection AddAgentSplicePersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Registered unconditionally and resolved only by the two hosted services below, both of
        // which stand down when nothing is retained. A registration is not a connection: with
        // persistence off nothing ever asks for a context, so no provider is initialised and no
        // database file appears.
        services.AddDbContextFactory<AgentSpliceDbContext>((provider, builder) =>
            Configure(builder, Options(provider)));

        services.AddSingleton<QueuedExchangeRecordSink>();

        services.AddSingleton<IExchangeRecordSink>(provider =>
            Retains(Options(provider))
                ? provider.GetRequiredService<QueuedExchangeRecordSink>()
                : new NullExchangeRecordSink());

        // Ordered. The initializer completes its migration in StartAsync, so the writer cannot reach
        // a database whose tables do not exist yet.
        services.AddHostedService<MetadataStoreInitializer>();
        services.AddHostedService<MetadataPersistenceService>();

        return services;
    }

    /// <summary>
    /// Whether this deployment retains anything.
    /// </summary>
    /// <remarks>
    /// Both settings have to agree. A configured store with metadata capture switched off retains
    /// nothing, and treating that as "persistence enabled" would report
    /// <see cref="Domain.Exchanges.ContentRetentionState.MetadataOnly"/> on every exchange while the
    /// store stayed empty.
    /// </remarks>
    internal static bool Retains(AgentSpliceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Persistence.Mode != PersistenceMode.None && options.Capture.MetadataEnabled;
    }

    private static AgentSpliceOptions Options(IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<AgentSpliceOptions>>().Value;

    /// <summary>Points the context at the configured store.</summary>
    /// <remarks>
    /// Runs when the options are built, which a host does at startup whether or not anything will
    /// ever create a context — so the no-store case has to be configured rather than rejected. It
    /// gets a provider with no connection string: enough for the options to be valid, and nothing to
    /// open. Both hosted services stand down when nothing is retained, so no context is created and
    /// no file appears.
    ///
    /// The unimplemented-mode branch is written out even though configuration validation rejects
    /// such a mode before readiness. Falling through to SQLite would mean a deployment that asked for
    /// PostgreSQL got a local file and no indication that it had.
    /// </remarks>
    private static void Configure(DbContextOptionsBuilder builder, AgentSpliceOptions options)
    {
        if (!Retains(options))
        {
            builder.UseSqlite();
            return;
        }

        if (options.Persistence.Mode != PersistenceMode.Sqlite)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"agentsplice:persistence:mode '{options.Persistence.Mode}' has no provider in this build."));
        }

        builder.UseSqlite(options.Persistence.ConnectionString);
    }
}
