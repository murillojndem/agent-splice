using AgentSplice.Application.Errors;

namespace AgentSplice.Application.Protocols;

/// <summary>
/// The outcome of reading a completion request: an envelope, or the error the client should see.
/// </summary>
/// <remarks>
/// A result rather than an exception. Validation failure is an ordinary, expected outcome on a
/// public endpoint, and modelling it as control flow keeps the error a value the orchestrator can
/// record and translate rather than something it has to catch.
/// </remarks>
public sealed record ChatCompletionReadResult
{
    private ChatCompletionReadResult()
    {
    }

    /// <summary>The parsed envelope, or <c>null</c> when the request was not valid.</summary>
    public ChatCompletionEnvelope? Envelope { get; private init; }

    /// <summary>The error to report, or <c>null</c> when the request was valid.</summary>
    public GatewayError? Error { get; private init; }

    /// <summary>True when the request parsed and validated.</summary>
    public bool Succeeded => Envelope is not null;

    /// <summary>Records a valid request.</summary>
    public static ChatCompletionReadResult Valid(ChatCompletionEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return new ChatCompletionReadResult { Envelope = envelope };
    }

    /// <summary>Records an invalid request.</summary>
    public static ChatCompletionReadResult Invalid(GatewayError gatewayError)
    {
        ArgumentNullException.ThrowIfNull(gatewayError);
        return new ChatCompletionReadResult { Error = gatewayError };
    }
}
