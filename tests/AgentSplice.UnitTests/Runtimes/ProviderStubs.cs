using AgentSplice.Application.Runtimes;

namespace AgentSplice.UnitTests.Runtimes;

/// <summary>An <see cref="IHttpClientFactory"/> that answers every request with one scripted response.</summary>
/// <remarks>
/// Shared by the tests that drive the LM Studio provider through its public surface, so what they
/// prove is the provider's behaviour with a transport that does exactly what the script says.
/// </remarks>
internal sealed class StubHttpClientFactory(HttpResponseMessage response) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new StubHandler(response));

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}

/// <summary>Resolves no credential, as for a runtime that takes none.</summary>
internal sealed class NoCredentialResolver : IRuntimeApiKeyResolver
{
    public RuntimeCredential Resolve(RuntimeTarget target) => RuntimeCredential.None;
}
