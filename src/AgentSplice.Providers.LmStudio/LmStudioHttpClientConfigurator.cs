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
    private readonly RuntimeRegistry runtimes;

    public LmStudioHttpClientConfigurator(RuntimeRegistry runtimes)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        this.runtimes = runtimes;
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
            });
    }
}
