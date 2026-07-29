using AgentSplice.Domain.Observations;
using AgentSplice.Domain.Runtimes;

namespace AgentSplice.Application.Runtimes;

/// <summary>
/// A classified upstream failure, carrying only bounded detail.
/// </summary>
/// <remarks>
/// Never holds an exception, a message, a hostname, or a response body. Those are the four channels
/// through which a credential, an internal address, or model output leaks into an error envelope or
/// a trace (docs/SECURITY.md "Trace safety").
/// </remarks>
public sealed record UpstreamFailure
{
    private UpstreamFailure()
    {
    }

    /// <summary>Why the call did not produce a usable answer.</summary>
    public UpstreamFailureReason Reason { get; private init; }

    /// <summary>Which budget elapsed, when <see cref="Reason"/> is <see cref="UpstreamFailureReason.Timeout"/>.</summary>
    public TimeoutPhase? Phase { get; private init; }

    /// <summary>The upstream status, when the runtime answered with one.</summary>
    public int? StatusCode { get; private init; }

    /// <summary>Sanitised supporting detail. Never a message, a host, or a body.</summary>
    public SafeDetails Details { get; private init; } = SafeDetails.Empty;

    /// <summary>How this failure should be reported as runtime health (FR-HEALTH-004).</summary>
    public RuntimeHealthStatus HealthStatus => Reason switch
    {
        UpstreamFailureReason.AuthenticationRejected => RuntimeHealthStatus.AuthenticationFailed,
        UpstreamFailureReason.InvalidResponse or UpstreamFailureReason.ResponseTooLarge =>
            RuntimeHealthStatus.IncompatibleResponse,
        UpstreamFailureReason.Unreachable or UpstreamFailureReason.Timeout =>
            RuntimeHealthStatus.Unreachable,

        // A caller that gave up says nothing about the runtime, so health stays undetermined
        // rather than being downgraded on the strength of our own cancellation.
        _ => RuntimeHealthStatus.Unknown,
    };

    /// <summary>Creates a classified failure.</summary>
    public static UpstreamFailure Create(
        UpstreamFailureReason reason,
        TimeoutPhase? phase = null,
        int? statusCode = null,
        SafeDetails? details = null)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown upstream failure reason.");
        }

        if (phase is { } elapsed && !Enum.IsDefined(elapsed))
        {
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown timeout phase.");
        }

        if (reason == UpstreamFailureReason.Timeout && phase is null)
        {
            throw new ArgumentException(
                "A timeout must record which phase elapsed; 'timed out' alone is not attributable (FR-CHAT-008).",
                nameof(phase));
        }

        if (reason != UpstreamFailureReason.Timeout && phase is not null)
        {
            throw new ArgumentException(
                "Only a timeout has a phase.",
                nameof(phase));
        }

        if (statusCode is { } status && status is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statusCode),
                status,
                "An HTTP status code must be in the range 100 to 599.");
        }

        return new UpstreamFailure
        {
            Reason = reason,
            Phase = phase,
            StatusCode = statusCode,
            Details = details ?? SafeDetails.Empty,
        };
    }

    /// <summary>Creates a timeout failure for the phase whose budget elapsed.</summary>
    public static UpstreamFailure Timeout(TimeoutPhase phase) =>
        Create(UpstreamFailureReason.Timeout, phase);
}
