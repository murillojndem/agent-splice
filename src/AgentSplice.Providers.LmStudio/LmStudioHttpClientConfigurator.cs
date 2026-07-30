using System.Collections.Frozen;
using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Identifiers;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace AgentSplice.Providers.LmStudio;

/// <summary>
/// Applies each runtime's transport policy to the named client that serves it.
/// </summary>
/// <remarks>
/// Named rather than typed, and configured on demand, so a client exists for exactly the runtimes
/// configuration declares — without the registration code having to read configuration before the
/// host has finished assembling it.
/// </remarks>
internal sealed class LmStudioHttpClientConfigurator : IConfigureNamedOptions<HttpClientFactoryOptions>
{
    /// <summary>
    /// Headers whose values must never appear in a log, whatever the level.
    /// </summary>
    /// <remarks>
    /// <c>IHttpClientFactory</c> logs request and response headers at <c>Trace</c>, so without this
    /// the runtime's bearer token is written verbatim to any sink an operator has enabled — a
    /// disclosure that has nothing to do with AgentSplice's own logging and would survive every
    /// precaution taken elsewhere in the request path.
    /// </remarks>
    private static readonly FrozenSet<string> RedactedHeaders = new[]
    {
        "authorization",
        "proxy-authorization",
        "www-authenticate",
        "proxy-authenticate",
        "cookie",
        "set-cookie",
        "api-key",
        "x-api-key",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly RuntimeRegistry runtimes;
    private readonly TimeProvider timeProvider;

    public LmStudioHttpClientConfigurator(RuntimeRegistry runtimes, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.runtimes = runtimes;
        this.timeProvider = timeProvider;
    }

    public void Configure(HttpClientFactoryOptions options)
    {
        // The unnamed client is not ours to configure.
    }

    public void Configure(string? name, HttpClientFactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (name is null
            || !name.StartsWith(LmStudioProviderRegistration.ClientNamePrefix, StringComparison.Ordinal))
        {
            return;
        }

        var runtimeId = name[LmStudioProviderRegistration.ClientNamePrefix.Length..];

        // Applied before the early return, so a client whose runtime cannot be resolved still never
        // logs a credential.
        options.ShouldRedactHeaderValue = RedactedHeaders.Contains;

        if (!RuntimeEndpointId.TryCreate(runtimeId, out var id) || runtimes.Find(id) is not { } target)
        {
            return;
        }

        options.HttpClientActions.Add(client =>
            // Phase attribution depends on knowing which of our own budgets elapsed. The 100 s
            // default throws a TaskCanceledException indistinguishable from a client disconnect,
            // which would make FR-CHAT-008 unimplementable and would report every disconnect as a
            // timeout.
            client.Timeout = Timeout.InfiniteTimeSpan);

        options.HttpMessageHandlerBuilderActions.Add(builder =>
            builder.PrimaryHandler = new SocketsHttpHandler
            {
                ConnectTimeout = target.Timeouts.Connect,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),

                // A redirect would silently change which runtime answered, and would send the
                // configured bearer token to whatever host the redirect named.
                AllowAutoRedirect = false,

                // A local runtime has no business being reached through a system proxy.
                UseProxy = false,

                // Taken over solely to time connection establishment, which is otherwise invisible
                // to the request path. The handler still applies ConnectTimeout to this callback, so
                // phase attribution is unaffected.
                ConnectCallback = (context, cancellationToken) =>
                    UpstreamConnectTiming.ConnectAsync(context, timeProvider, cancellationToken),
            });
    }
}
