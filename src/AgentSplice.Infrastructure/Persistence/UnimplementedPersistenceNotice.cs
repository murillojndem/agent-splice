using AgentSplice.Application.Configuration;
using AgentSplice.Application.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSplice.Infrastructure.Persistence;

/// <summary>
/// Says plainly, at startup, that a configured persistence mode is not yet implemented.
/// </summary>
/// <remarks>
/// The shipped configuration selects SQLite, because FR-DATA-002 makes it the local default
/// <em>when persistence is enabled</em>, and that default should not churn as stages land. But no
/// store exists yet, so an operator reading their own settings would reasonably expect a database
/// file to appear and exchanges to accumulate. Nothing happens, and nothing says so.
///
/// One startup line is the whole mitigation. The alternative — changing the shipped default to
/// <c>None</c> — would make the setting honest today and wrong again the moment Stage 1C ships, and
/// would contradict the documented default in the meantime.
/// </remarks>
public sealed class UnimplementedPersistenceNotice : IHostedService
{
    private readonly IOptions<AgentSpliceOptions> options;
    private readonly ILogger<UnimplementedPersistenceNotice> logger;

    /// <summary>Creates the notice.</summary>
    public UnimplementedPersistenceNotice(
        IOptions<AgentSpliceOptions> options,
        ILogger<UnimplementedPersistenceNotice> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        this.options = options;
        this.logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var mode = options.Value.Persistence.Mode;

        if (mode != PersistenceMode.None)
        {
            logger.LogWarning(
                GatewayEventIds.PersistenceNotImplemented,
                "Metadata persistence is configured as {PersistenceMode} but no store is implemented in this build. Exchanges are observable while in flight and are not retained.",
                mode);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
