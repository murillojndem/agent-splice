using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using AgentSplice.Application.Configuration;
using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Observations;
using Microsoft.Extensions.Options;

namespace AgentSplice.Providers.LmStudio;

/// <summary>
/// Speaks to an LM Studio runtime over its OpenAI-compatible surface.
/// </summary>
/// <remarks>
/// The credential is resolved here, at the moment the request is built, rather than being carried in
/// from orchestration. That keeps a secret out of routing decisions, exchange records, and
/// observability, where every value is a potential disclosure (docs/SECURITY.md).
///
/// No transport exception escapes: each is classified into an <see cref="UpstreamFailure"/>, which
/// is what allows <c>AgentSplice.Application</c> to have no dependency on <c>System.Net.Http</c>.
/// </remarks>
public sealed class LmStudioModelRuntimeProvider : IModelRuntimeProvider
{
    private readonly IHttpClientFactory clientFactory;
    private readonly IRuntimeApiKeyResolver apiKeys;
    private readonly IOptions<AgentSpliceOptions> options;

    /// <summary>Creates the provider.</summary>
    public LmStudioModelRuntimeProvider(
        IHttpClientFactory clientFactory,
        IRuntimeApiKeyResolver apiKeys,
        IOptions<AgentSpliceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(apiKeys);
        ArgumentNullException.ThrowIfNull(options);

        this.clientFactory = clientFactory;
        this.apiKeys = apiKeys;
        this.options = options;
    }

    /// <inheritdoc />
    public string ProviderKey => LmStudioProviderRegistration.ProviderKey;

    /// <inheritdoc />
    public async Task<RuntimeModelListResult> ListModelsAsync(
        RuntimeTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        // The client token is the root of the chain, so a caller giving up cancels the upstream
        // request rather than merely abandoning our own read.
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(target.Timeouts.Total);

        using var responseHeaders = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
        responseHeaders.CancelAfter(target.Timeouts.ResponseHeaders);

        try
        {
            return await ListModelsCoreAsync(target, total, responseHeaders).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return RuntimeModelListResult.Failed(UpstreamFailureClassifier.ClassifyCancellation(
                cancellationToken,
                total.Token,
                responseHeaders.Token));
        }
        catch (HttpRequestException exception)
        {
            return RuntimeModelListResult.Failed(
                UpstreamFailureClassifier.ClassifyRequestFailure(exception));
        }
        catch (IOException)
        {
            // The connection dropped mid-body. The runtime was reachable, so this is a truncated
            // answer rather than an unreachable endpoint.
            return Invalid("body.truncated");
        }
    }

    private async Task<RuntimeModelListResult> ListModelsCoreAsync(
        RuntimeTarget target,
        CancellationTokenSource total,
        CancellationTokenSource responseHeaders)
    {
        // Not disposed: the factory owns the pooled handler, and disposing the wrapper would only
        // obscure that.
        var client = clientFactory.CreateClient(LmStudioProviderRegistration.ClientNameFor(target.Id));
        using var request = new HttpRequestMessage(HttpMethod.Get, target.ResolvePath("models"));

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        Authorise(request, target);

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, responseHeaders.Token)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // The body is discarded: an authentication error page can echo the key or hint at its
            // shape, and it is never useful to the client.
            return RuntimeModelListResult.Failed(UpstreamFailure.Create(
                UpstreamFailureReason.AuthenticationRejected,
                statusCode: (int)response.StatusCode));
        }

        if (!response.IsSuccessStatusCode)
        {
            return Invalid("status." + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var body = await ReadBodyAsync(response, total.Token).ConfigureAwait(false);

        if (body.ExceededLimit)
        {
            return RuntimeModelListResult.Failed(UpstreamFailure.Create(
                UpstreamFailureReason.ResponseTooLarge,
                details: SafeDetails.Create("upstream.catalogue", "body.too_large")));
        }

        return body.Truncated
            ? Invalid("body.truncated")
            : LmStudioModelCatalogueReader.Read(new ReadOnlySequence<byte>(body.Body!));
    }

    private async Task<BoundedBodyReader.Result> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        return await BoundedBodyReader.ReadAsync(
                stream,
                options.Value.Limits.MaxCatalogueBodyBytes,
                response.Content.Headers.ContentLength,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void Authorise(HttpRequestMessage request, RuntimeTarget target)
    {
        var credential = apiKeys.Resolve(target);

        if (credential.HasValue)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Reveal());
        }
    }

    private static RuntimeModelListResult Invalid(string detail) =>
        RuntimeModelListResult.Failed(UpstreamFailure.Create(
            UpstreamFailureReason.InvalidResponse,
            details: SafeDetails.Create("upstream.catalogue", detail)));
}
