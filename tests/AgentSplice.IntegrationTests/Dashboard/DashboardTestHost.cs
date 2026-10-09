using AgentSplice.IntegrationTests.Hosting;
using AgentSplice.TestSupport.Logging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentSplice.IntegrationTests.Dashboard;

/// <summary>
/// The production host, booted with dashboard settings and a client that does not follow redirects.
/// </summary>
/// <remarks>
/// No fake upstream: nothing under test here reaches a runtime, and the cases that prove the APIs are
/// unaffected use routes that answer without one. Redirects are not followed because several of them
/// are the behaviour under test.
/// </remarks>
internal sealed class DashboardTestHost : IDisposable
{
    private readonly AgentSpliceApplicationFactory factory;

    internal DashboardTestHost(
        Action<Dictionary<string, string?>>? configure = null,
        CapturingLoggerProvider? logs = null)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        configure?.Invoke(settings);

        factory = new AgentSpliceApplicationFactory(
            settings,
            logs is null ? null : services => services.AddSingleton<ILoggerProvider>(logs));

        Client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    internal HttpClient Client { get; }

    /// <summary>The in-process server, for requests <see cref="HttpClient"/> would normalise before sending.</summary>
    internal TestServer Server => factory.Server;

    internal async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

        return await Client.SendAsync(request);
    }

    internal Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path);

    /// <summary>A header's value, wherever <see cref="HttpClient"/> filed it; null when absent.</summary>
    internal static string? Header(HttpResponseMessage response, string name)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Headers.TryGetValues(name, out var values)
            || response.Content.Headers.TryGetValues(name, out values))
        {
            return string.Join(", ", values);
        }

        return null;
    }

    public void Dispose()
    {
        Client.Dispose();
        factory.Dispose();
    }
}
