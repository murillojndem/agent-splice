using AgentSplice.Domain.Exchanges;

namespace AgentSplice.Application.Errors;

/// <summary>
/// A client-facing error: its stable code, its coarse type, the status it produces, and a constant
/// message (docs/API.md, docs/SPECIFICATION.md section 10.3).
/// </summary>
/// <remarks>
/// <see cref="Message"/> is always a constant chosen by AgentSplice. It is never derived from an
/// upstream message, a response body, an exception, or a URL, because each of those can carry a
/// credential, an internal hostname, or model output into a client response
/// (docs/SECURITY.md "Trace safety").
/// </remarks>
public sealed record GatewayError
{
    private GatewayError()
    {
    }

    /// <summary>The stable machine-readable code from <see cref="ErrorCodes"/>.</summary>
    public string Code { get; private init; } = string.Empty;

    /// <summary>The coarse category from <see cref="ErrorTypes"/>.</summary>
    public string Type { get; private init; } = string.Empty;

    /// <summary>The HTTP status this error produces.</summary>
    public int StatusCode { get; private init; }

    /// <summary>A constant, safe explanation.</summary>
    public string Message { get; private init; } = string.Empty;

    /// <summary>The offending request field, when one can be named.</summary>
    public string? Param { get; private init; }

    /// <summary>The internal classification this error translates, when it translates one.</summary>
    public FailureClass? FailureClass { get; private init; }

    /// <summary>Creates a client-facing error.</summary>
    public static GatewayError Create(
        string code,
        string type,
        int statusCode,
        string message,
        string? param = null,
        FailureClass? failureClass = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statusCode),
                statusCode,
                "An HTTP status code must be in the range 100 to 599.");
        }

        if (!ErrorCodes.All.Contains(code))
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"'{code}' is not a published error code. Clients match on these strings, so an ad-hoc code would be an unannounced contract change."),
                nameof(code));
        }

        if (!ErrorTypes.All.Contains(type))
        {
            throw new ArgumentException(
                FormattableString.Invariant($"'{type}' is not a published error type."),
                nameof(type));
        }

        if (failureClass is { } declared && !Enum.IsDefined(declared))
        {
            throw new ArgumentOutOfRangeException(nameof(failureClass), declared, "Unknown failure class.");
        }

        return new GatewayError
        {
            Code = code,
            Type = type,
            StatusCode = statusCode,
            Message = message,
            Param = param,
            FailureClass = failureClass,
        };
    }
}
