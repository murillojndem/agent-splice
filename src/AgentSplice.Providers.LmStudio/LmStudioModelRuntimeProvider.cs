using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using AgentSplice.Application.Configuration;
using AgentSplice.Application.Protocols;
using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Exchanges;
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
    private const string OpenAiJsonMediaType = "application/json";
    private const string ForwardedRequestIdHeader = GatewayHeaderPolicy.CorrelationHeader;

    private readonly IHttpClientFactory clientFactory;
    private readonly IRuntimeApiKeyResolver apiKeys;
    private readonly IOptions<AgentSpliceOptions> options;
    private readonly TimeProvider timeProvider;

    /// <summary>Creates the provider.</summary>
    public LmStudioModelRuntimeProvider(
        IHttpClientFactory clientFactory,
        IRuntimeApiKeyResolver apiKeys,
        IOptions<AgentSpliceOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(apiKeys);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.clientFactory = clientFactory;
        this.apiKeys = apiKeys;
        this.options = options;
        this.timeProvider = timeProvider;
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

    /// <inheritdoc />
    public async Task<ProviderCompletionResult> CompleteAsync(
        ProviderCompletionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = request.Target;

        // The client token is the root, so a disconnect aborts the upstream request rather than
        // merely abandoning our read of it (FR-CHAT-006).
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(target.Timeouts.Total);

        using var responseHeaders = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
        responseHeaders.CancelAfter(target.Timeouts.ResponseHeaders);

        try
        {
            return await CompleteCoreAsync(request, total, responseHeaders).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ProviderCompletionResult.Failed(UpstreamFailureClassifier.ClassifyCancellation(
                cancellationToken,
                total.Token,
                responseHeaders.Token));
        }
        catch (HttpRequestException exception)
        {
            return ProviderCompletionResult.Failed(
                UpstreamFailureClassifier.ClassifyRequestFailure(exception));
        }
        catch (IOException)
        {
            return ProviderCompletionResult.Failed(UpstreamFailure.Create(
                UpstreamFailureReason.InvalidResponse,
                details: SafeDetails.Create("upstream.completion", "body.truncated")));
        }
    }

    /// <inheritdoc />
    public async Task<ProviderStreamResult> StreamAsync(
        ProviderCompletionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = request.Target;

        // Not disposed here. On success the budgets outlive this method along with the response, and
        // the returned stream owns all four; disposing them at the end of the call would cancel the
        // stream the caller is about to read.
        var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(target.Timeouts.Total);

        var responseHeaders = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
        responseHeaders.CancelAfter(target.Timeouts.ResponseHeaders);

        var idle = CancellationTokenSource.CreateLinkedTokenSource(total.Token);

        try
        {
            var result = await StreamCoreAsync(request, total, responseHeaders, idle, cancellationToken)
                .ConfigureAwait(false);

            // Only an opened stream takes ownership of the budgets; anything else leaves them here
            // to be released, or they outlive the request with a live timer attached.
            return result.Opened ? result : Abandon(result);
        }
        catch (OperationCanceledException)
        {
            return Abandon(
                ProviderStreamResult.Failed(UpstreamFailureClassifier.ClassifyCancellation(
                    cancellationToken,
                    total.Token,
                    responseHeaders.Token,
                    idle.Token)));
        }
        catch (HttpRequestException exception)
        {
            return Abandon(
                ProviderStreamResult.Failed(UpstreamFailureClassifier.ClassifyRequestFailure(exception)));
        }
        catch (IOException)
        {
            return Abandon(ProviderStreamResult.Failed(UpstreamFailure.Create(
                UpstreamFailureReason.InvalidResponse,
                details: SafeDetails.Create("upstream.stream", "connection.lost"))));
        }

        ProviderStreamResult Abandon(ProviderStreamResult failure)
        {
            idle.Dispose();
            responseHeaders.Dispose();
            total.Dispose();

            return failure;
        }
    }

    private async Task<ProviderStreamResult> StreamCoreAsync(
        ProviderCompletionRequest request,
        CancellationTokenSource total,
        CancellationTokenSource responseHeaders,
        CancellationTokenSource idle,
        CancellationToken clientToken)
    {
        var target = request.Target;
        var client = clientFactory.CreateClient(LmStudioProviderRegistration.ClientNameFor(target.Id));

        using var message = new HttpRequestMessage(HttpMethod.Post, target.ResolvePath("chat/completions"))
        {
            Content = new ReadOnlyMemoryContent(request.Body),
        };

        message.Content.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(request.AcceptMediaType));

        // A runtime may still answer with an ordinary body — an error, or a build that ignores the
        // streaming flag — and that answer is relayed rather than refused.
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(OpenAiJsonMediaType));

        message.Headers.TryAddWithoutValidation(ForwardedRequestIdHeader, request.RequestId.Value);
        Authorise(message, target);

        var response = await client
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, responseHeaders.Token)
            .ConfigureAwait(false);

        // Disarmed the moment headers arrive. Left armed it would fire during any stream that
        // outlives the header budget, and the classifier would then report every mid-stream stall as
        // a runtime that was slow to respond.
        responseHeaders.CancelAfter(Timeout.InfiniteTimeSpan);

        var metadata = UpstreamResponseMetadata.Create(
            (int)response.StatusCode,
            timeProvider.GetUtcNow(),
            response.Content.Headers.ContentType?.ToString(),
            ReadUpstreamRequestId(response));

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // Withheld for the same reason as on the buffered path: an authentication error page can
            // echo the gateway's key or hint at its shape, and it is the gateway's key, not the
            // client's.
            response.Dispose();

            return ProviderStreamResult.Failed(
                UpstreamFailure.Create(
                    UpstreamFailureReason.AuthenticationRejected,
                    statusCode: (int)response.StatusCode),
                metadata);
        }

        var body = await response.Content.ReadAsStreamAsync(total.Token).ConfigureAwait(false);

        return ProviderStreamResult.FromResponse(
            metadata,
            new LmStudioUpstreamResponseBody(
                response,
                body,
                total,
                responseHeaders,
                idle,
                target.Timeouts.IdleStream,
                clientToken),
            RelayableHeaders(response))
            .WithConnection(ReadConnectTiming(message));
    }

    private async Task<ProviderCompletionResult> CompleteCoreAsync(
        ProviderCompletionRequest request,
        CancellationTokenSource total,
        CancellationTokenSource responseHeaders)
    {
        var target = request.Target;
        var client = clientFactory.CreateClient(LmStudioProviderRegistration.ClientNameFor(target.Id));

        using var message = new HttpRequestMessage(HttpMethod.Post, target.ResolvePath("chat/completions"))
        {
            Content = new ReadOnlyMemoryContent(request.Body),
        };

        message.Content.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(OpenAiJsonMediaType));

        // Forwarded so a runtime log line can be tied back to an AgentSplice exchange. It is the
        // cheapest correlation link in the product and carries no content.
        message.Headers.TryAddWithoutValidation(ForwardedRequestIdHeader, request.RequestId.Value);
        Authorise(message, target);

        // ResponseHeadersRead even though this path buffers, so that headers and first byte are
        // boundaries actually observed rather than moments inferred after the body arrived.
        using var response = await client
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, responseHeaders.Token)
            .ConfigureAwait(false);

        var metadata = UpstreamResponseMetadata.Create(
            (int)response.StatusCode,
            timeProvider.GetUtcNow(),
            response.Content.Headers.ContentType?.ToString(),
            ReadUpstreamRequestId(response));

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // The body is discarded rather than relayed: an authentication error page can echo the
            // key or hint at its shape, and the credential is the gateway's, not the client's.
            return ProviderCompletionResult.Failed(
                UpstreamFailure.Create(
                    UpstreamFailureReason.AuthenticationRejected,
                    statusCode: (int)response.StatusCode),
                metadata);
        }

        // Stamped inside the callback rather than after the read returns. Reading the clock
        // afterwards names the moment the body finished — or the moment it failed — and calls it the
        // first byte, which for a slow generation is wrong by the whole length of the response
        // (ADR 0010). Assigned once: a later read must not overwrite the boundary it is not.
        DateTimeOffset? firstByteAt = null;

        void ObserveFirstByte() => firstByteAt ??= timeProvider.GetUtcNow();

        BoundedBodyReader.Result body;

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(total.Token).ConfigureAwait(false);

            body = await BoundedBodyReader.ReadAsync(
                    stream,
                    options.Value.Limits.MaxUpstreamCompletionBodyBytes,
                    response.Content.Headers.ContentLength,
                    ObserveFirstByte,
                    total.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            // Classified here rather than by the outer handler, because by this point the runtime
            // has already answered: it was reachable, it chose a status, and it then died partway
            // through its own body. A connection that dies mid-body surfaces as an IOException or an
            // HttpRequestException depending on how the peer closed and which side noticed first, so
            // letting it reach a handler that cannot tell the phase apart makes the reported cause a
            // race — and "the runtime could not be reached" sends an operator to the network when
            // the runtime is right there, failing.
            return ProviderCompletionResult.Failed(
                UpstreamFailure.Create(
                    UpstreamFailureReason.InvalidResponse,
                    details: SafeDetails.Create("upstream.completion", "body.truncated")),
                metadata,
                firstByteAt);
        }

        if (body.ExceededLimit)
        {
            return ProviderCompletionResult.Failed(
                UpstreamFailure.Create(
                    UpstreamFailureReason.ResponseTooLarge,
                    details: SafeDetails.Create("upstream.completion", "body.too_large")),
                metadata,
                firstByteAt);
        }

        if (body.Truncated)
        {
            return ProviderCompletionResult.Failed(
                UpstreamFailure.Create(
                    UpstreamFailureReason.InvalidResponse,
                    details: SafeDetails.Create("upstream.completion", "body.truncated")),
                metadata,
                firstByteAt);
        }

        return ProviderCompletionResult.FromResponse(
            metadata,
            body.Body!,
            firstByteAt,
            timeProvider.GetUtcNow(),
            RelayableHeaders(response))
            .WithConnection(ReadConnectTiming(message));
    }

    /// <summary>
    /// Reads back what the connect callback recorded, if this request opened a connection.
    /// </summary>
    /// <remarks>
    /// Absent for a request served by a pooled connection, which is the ordinary case after the
    /// first. Absence means no connection was established, not that establishing one was free.
    /// </remarks>
    private static UpstreamConnectObservation? ReadConnectTiming(HttpRequestMessage message) =>
        message.Options.TryGetValue(UpstreamConnectTiming.OptionsKey, out var timing)
            ? UpstreamConnectObservation.Create(timing.StartedAt, timing.CompletedAt)
            : null;

    /// <summary>Selects the response headers the policy permits relaying to the client.</summary>
    private static Dictionary<string, string> RelayableHeaders(HttpResponseMessage response)
    {
        var relayed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (GatewayHeaderPolicy.MayRelayToClient(header.Key))
            {
                relayed[header.Key] = string.Join(", ", header.Value);
            }
        }

        return relayed;
    }

    private static string? ReadUpstreamRequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues(ForwardedRequestIdHeader, out var values)
            ? values.FirstOrDefault()
            : null;

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

    /// <summary>
    /// Reads a catalogue body, treating a connection that dies mid-body as a truncated answer.
    /// </summary>
    /// <remarks>
    /// The runtime has already answered by this point, so the failure cannot mean it was
    /// unreachable. Which exception the platform raises depends on how the peer closed, and letting
    /// that decide the reported cause would make the diagnosis a race.
    /// </remarks>
    private async Task<BoundedBodyReader.Result> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await BoundedBodyReader.ReadAsync(
                    stream,
                    options.Value.Limits.MaxCatalogueBodyBytes,
                    response.Content.Headers.ContentLength,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            return BoundedBodyReader.Result.PrematureEnd();
        }
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
