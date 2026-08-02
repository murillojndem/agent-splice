using System.Collections.Frozen;
using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Exchanges;

namespace AgentSplice.Application.Errors;

/// <summary>
/// The single mapping from an internal failure class to what a client sees
/// (docs/API.md, docs/SPECIFICATION.md section 10.3).
/// </summary>
/// <remarks>
/// One table, total over <see cref="FailureClass"/>, so a new failure class cannot be introduced
/// without deciding what it means to a client. Spread across call sites, the same mapping drifts:
/// two places translate the same condition into two different statuses and neither is obviously
/// wrong.
///
/// Every message is a compile-time constant. None is derived from an upstream message, a response
/// body, an exception, or a URL, because each of those can carry a credential, an internal hostname,
/// or model output into a client response (docs/SECURITY.md "Trace safety").
/// </remarks>
public static class GatewayErrorCatalogue
{
    /// <summary>
    /// The status recorded for a client disconnect.
    /// </summary>
    /// <remarks>
    /// Never written: by definition the socket is gone. It exists so the condition has a
    /// distinguishable value in evidence and in metrics rather than being folded into a 5xx.
    /// </remarks>
    public const int ClientClosedRequestStatus = 499;

    private static readonly FrozenDictionary<FailureClass, GatewayError> ByFailureClass =
        new Dictionary<FailureClass, GatewayError>
        {
            [FailureClass.InvalidRequest] = Create(
                FailureClass.InvalidRequest,
                ErrorCodes.InvalidRequest,
                ErrorTypes.InvalidRequest,
                400,
                "The request is not valid for this endpoint."),

            [FailureClass.ModelNotFound] = Create(
                FailureClass.ModelNotFound,
                ErrorCodes.ModelNotFound,
                ErrorTypes.InvalidRequest,
                404,
                "The requested model is not available through this gateway.",
                "model"),

            [FailureClass.RuntimeNotFound] = Create(
                FailureClass.RuntimeNotFound,
                ErrorCodes.RuntimeNotFound,
                ErrorTypes.Configuration,

                // A configuration defect, not an availability problem: the runtime was never
                // contacted, so reporting it as unreachable would send an operator to the wrong
                // place.
                503,
                "The resolved runtime is served by no configured provider module."),

            [FailureClass.RuntimeUnavailable] = Create(
                FailureClass.RuntimeUnavailable,
                ErrorCodes.RuntimeUnavailable,
                ErrorTypes.UpstreamUnavailable,
                502,
                "The runtime could not be reached."),

            [FailureClass.RuntimeAuthenticationFailed] = Create(
                FailureClass.RuntimeAuthenticationFailed,
                ErrorCodes.RuntimeAuthenticationFailed,
                ErrorTypes.UpstreamAuthentication,

                // Never 401 or 403. The credential is the gateway's, not the client's, and echoing
                // the runtime's status would tell a client to fix a key it does not own.
                502,
                "The runtime rejected the gateway's credentials."),

            [FailureClass.UpstreamTimeout] = Create(
                FailureClass.UpstreamTimeout,
                ErrorCodes.UpstreamTimeout,
                ErrorTypes.UpstreamTimeout,
                504,
                "The runtime did not answer within the configured timeout."),

            [FailureClass.InvalidUpstreamResponse] = Create(
                FailureClass.InvalidUpstreamResponse,
                ErrorCodes.InvalidUpstreamResponse,
                ErrorTypes.UpstreamProtocol,
                502,
                "The runtime's response could not be read."),

            [FailureClass.InvalidUpstreamStream] = Create(
                FailureClass.InvalidUpstreamStream,
                ErrorCodes.InvalidUpstreamStream,
                ErrorTypes.UpstreamProtocol,
                502,
                "The runtime's event stream violated the protocol."),

            [FailureClass.RequestCancelled] = Create(
                FailureClass.RequestCancelled,
                ErrorCodes.RequestCancelled,
                ErrorTypes.Cancellation,
                ClientClosedRequestStatus,
                "The client disconnected before the exchange completed."),

            [FailureClass.GatewayOverloaded] = Create(
                FailureClass.GatewayOverloaded,
                ErrorCodes.GatewayOverloaded,
                ErrorTypes.RateLimit,

                // 429 rather than 503, because this is a rate the caller can lower. It is also the
                // status every OpenAI SDK already backs off on, so an existing client handles it
                // without being taught anything new.
                429,
                "The gateway is serving as many completions as it will serve at once."),

            [FailureClass.PersistenceUnavailable] = Create(
                FailureClass.PersistenceUnavailable,
                ErrorCodes.PersistenceUnavailable,
                ErrorTypes.Internal,
                500,
                "Exchange metadata could not be recorded."),

            [FailureClass.InternalError] = Create(
                FailureClass.InternalError,
                ErrorCodes.InternalError,
                ErrorTypes.Internal,
                500,
                "The gateway failed to complete the request."),
        }.ToFrozenDictionary();

    /// <summary>The canonical client-facing error for a failure class.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The class has no entry, which is a defect.</exception>
    public static GatewayError For(FailureClass failureClass) =>
        ByFailureClass.TryGetValue(failureClass, out var error)
            ? error
            : throw new ArgumentOutOfRangeException(
                nameof(failureClass),
                failureClass,
                "No client-facing error is declared for this failure class.");

    /// <summary>Every declared failure class. Used by the totality test.</summary>
    public static IReadOnlyCollection<FailureClass> DeclaredFailureClasses => ByFailureClass.Keys;

    /// <summary>
    /// A validation failure with a specific explanation and, where possible, the offending field.
    /// </summary>
    /// <remarks>
    /// The one place a caller supplies a message. Validation messages have to be specific to be
    /// useful, and they describe the client's own request, so they disclose nothing the client did
    /// not already send.
    /// </remarks>
    public static GatewayError InvalidRequest(string message, string? param = null) =>
        GatewayError.Create(
            ErrorCodes.InvalidRequest,
            ErrorTypes.InvalidRequest,
            400,
            message,
            param,
            FailureClass.InvalidRequest);

    /// <summary>An administrative read named an exchange the store does not hold.</summary>
    /// <remarks>
    /// Carries no failure class. Nothing failed: a caller asked for a row that is not there, which
    /// is an ordinary answer on a read surface and not an exchange outcome. It is also the honest
    /// answer for a row that existed and was expired by retention.
    /// </remarks>
    public static GatewayError ExchangeNotFound { get; } = GatewayError.Create(
        ErrorCodes.ExchangeNotFound,
        ErrorTypes.InvalidRequest,
        404,
        "No exchange with that identifier is retained.");

    /// <summary>The deployment retains nothing, so a read of stored evidence has no answer.</summary>
    /// <remarks>
    /// Not an empty page. FR-DATA-001 makes ephemeral operation a supported deployment, so "no
    /// exchanges are stored" and "no exchanges happened" are both true statements about it and only
    /// one is what the caller asked. Returning 200 with an empty list would report the second, which
    /// is the kind of confident wrong answer this product exists to remove.
    ///
    /// 503 with a configuration type, matching RuntimeNotFound: the capability is not configured
    /// here, which is an operator fact rather than a fault.
    /// </remarks>
    public static GatewayError PersistenceDisabled { get; } = GatewayError.Create(
        ErrorCodes.PersistenceDisabled,
        ErrorTypes.Configuration,
        503,
        "This deployment retains no exchange metadata; set agentsplice:persistence:mode to enable it.");

    /// <summary>A query parameter on an administrative endpoint is not usable.</summary>
    /// <remarks>
    /// The message names the parameter rather than echoing its value: the value is caller-supplied
    /// text, and an error envelope is a place it must not be reflected into.
    /// </remarks>
    public static GatewayError InvalidQuery(string message, string? param = null) =>
        GatewayError.Create(
            ErrorCodes.InvalidQuery,
            ErrorTypes.InvalidRequest,
            400,
            message,
            param);

    /// <summary>A request body larger than the configured bound.</summary>
    public static GatewayError BodyTooLarge { get; } = GatewayError.Create(
        ErrorCodes.InvalidRequest,
        ErrorTypes.InvalidRequest,
        413,
        "The request body exceeds the configured maximum size.",
        failureClass: FailureClass.InvalidRequest);

    /// <summary>Model resolution could not be attempted because no runtime could be consulted.</summary>
    /// <remarks>
    /// Distinct from <see cref="FailureClass.ModelNotFound"/> on purpose. "The model does not exist"
    /// and "AgentSplice could not ask" are different facts, and reporting the first when the second
    /// is true is the misleading evidence this product exists to remove.
    /// </remarks>
    public static GatewayError DiscoveryUnavailable { get; } = GatewayError.Create(
        ErrorCodes.RuntimeUnavailable,
        ErrorTypes.UpstreamUnavailable,
        502,
        "No configured runtime could be reached to resolve the requested model.",
        failureClass: FailureClass.RuntimeUnavailable);

    /// <summary>Model discovery could not be attempted for any configured runtime.</summary>
    public static GatewayError ModelDiscoveryUnavailable { get; } = GatewayError.Create(
        ErrorCodes.RuntimeUnavailable,
        ErrorTypes.UpstreamUnavailable,
        502,
        "No configured runtime could be reached to list models.",
        failureClass: FailureClass.RuntimeUnavailable);

    /// <summary>Translates a classified upstream failure into what the client sees.</summary>
    public static GatewayError Translate(UpstreamFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return For(failure.Reason switch
        {
            UpstreamFailureReason.AuthenticationRejected => FailureClass.RuntimeAuthenticationFailed,
            UpstreamFailureReason.Timeout => FailureClass.UpstreamTimeout,
            UpstreamFailureReason.InvalidResponse or UpstreamFailureReason.ResponseTooLarge =>
                FailureClass.InvalidUpstreamResponse,
            UpstreamFailureReason.Cancelled => FailureClass.RequestCancelled,
            _ => FailureClass.RuntimeUnavailable,
        });
    }

    private static GatewayError Create(
        FailureClass failureClass,
        string code,
        string type,
        int statusCode,
        string message,
        string? param = null) =>
        GatewayError.Create(code, type, statusCode, message, param, failureClass);
}
