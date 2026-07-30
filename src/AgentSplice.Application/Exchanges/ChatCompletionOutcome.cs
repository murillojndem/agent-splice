using AgentSplice.Application.Errors;
using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Application.Exchanges;

/// <summary>
/// What the transport layer should send back, and the correlation identifiers to send with it.
/// </summary>
/// <remarks>
/// <see cref="ClientDisconnected"/> is a distinct state rather than a status code. Writing anything
/// to a socket the client has already closed throws, and setting a status after the response has
/// started throws over the top of the real cause, so "write nothing" has to be expressible.
/// </remarks>
public sealed record ChatCompletionOutcome
{
    private static readonly Dictionary<string, string> NoHeaders =
        new(StringComparer.OrdinalIgnoreCase);

    private ChatCompletionOutcome()
    {
    }

    /// <summary>True when the client is gone and nothing should be written.</summary>
    public bool ClientDisconnected { get; private init; }

    /// <summary>The status to send.</summary>
    public int StatusCode { get; private init; }

    /// <summary>The media type of <see cref="Body"/>.</summary>
    public string MediaType { get; private init; } = string.Empty;

    /// <summary>The bytes to send.</summary>
    public ReadOnlyMemory<byte> Body { get; private init; }

    /// <summary>The correlation token, returned on every response.</summary>
    public PublicRequestId RequestId { get; private init; }

    /// <summary>The exchange identity, returned on every completion response.</summary>
    public ExchangeId ExchangeId { get; private init; }

    /// <summary>The runtime that served the request, or <c>null</c> when none was resolved.</summary>
    public RuntimeEndpointId? Runtime { get; private init; }

    /// <summary>Upstream headers the relay policy permits forwarding.</summary>
    public IReadOnlyDictionary<string, string> RelayedHeaders { get; private init; } = NoHeaders;

    /// <summary>The error reported, or <c>null</c> when none was.</summary>
    public GatewayError? Error { get; private init; }

    /// <summary>The runtime answered and its answer is being relayed unchanged.</summary>
    public static ChatCompletionOutcome Relayed(
        ExchangeRecorder recorder,
        int statusCode,
        string mediaType,
        ReadOnlyMemory<byte> body,
        RuntimeEndpointId runtime,
        IReadOnlyDictionary<string, string> relayedHeaders)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        ArgumentNullException.ThrowIfNull(relayedHeaders);

        return new ChatCompletionOutcome
        {
            StatusCode = statusCode,
            MediaType = mediaType,
            Body = body,
            RequestId = recorder.RequestId,
            ExchangeId = recorder.ExchangeId,
            Runtime = runtime,
            RelayedHeaders = relayedHeaders,
        };
    }

    /// <summary>AgentSplice is reporting its own error.</summary>
    public static ChatCompletionOutcome Failed(
        ExchangeRecorder recorder,
        GatewayError gatewayError,
        string mediaType,
        ReadOnlyMemory<byte> body,
        RuntimeEndpointId? runtime)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(gatewayError);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        return new ChatCompletionOutcome
        {
            StatusCode = gatewayError.StatusCode,
            MediaType = mediaType,
            Body = body,
            RequestId = recorder.RequestId,
            ExchangeId = recorder.ExchangeId,
            Runtime = runtime,
            Error = gatewayError,
        };
    }

    /// <summary>The client went away; nothing is written.</summary>
    public static ChatCompletionOutcome Disconnected(ExchangeRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        return new ChatCompletionOutcome
        {
            ClientDisconnected = true,
            RequestId = recorder.RequestId,
            ExchangeId = recorder.ExchangeId,
        };
    }
}
