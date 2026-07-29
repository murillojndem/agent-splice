namespace AgentSplice.Api.Correlation;

/// <summary>
/// The gateway's own headers (docs/API.md "Gateway headers").
/// </summary>
/// <remarks>
/// Constants rather than literals because these names are a published contract that clients,
/// conformance reports, and issue templates match on, and because a contract test binds this list to
/// the document that declares it.
/// </remarks>
public static class GatewayHeaderNames
{
    /// <summary>Correlation token a client may supply.</summary>
    public const string ClientRequestId = "x-request-id";

    /// <summary>Correlation token AgentSplice returns on every response.</summary>
    public const string RequestId = "x-agentsplice-request-id";

    /// <summary>The exchange identifier, returned only for a completion exchange.</summary>
    public const string ExchangeId = "x-agentsplice-exchange-id";

    /// <summary>The OpenTelemetry trace identifier, returned only when a trace exists.</summary>
    public const string TraceId = "x-agentsplice-trace-id";

    /// <summary>The runtime that served the request, returned only once one was resolved.</summary>
    public const string Runtime = "x-agentsplice-runtime";
}
