namespace AgentSplice.Application.Runtimes;

/// <summary>
/// Why an upstream call did not produce a usable answer, expressed without any transport type.
/// </summary>
/// <remarks>
/// The provider module classifies its own exceptions into these values. That is what lets
/// <c>AgentSplice.Application</c> stay free of <c>System.Net.Http</c> — a boundary an architecture
/// test enforces — and it is what makes error translation testable without a socket.
/// </remarks>
public enum UpstreamFailureReason
{
    /// <summary>The runtime could not be reached: refused, unresolvable, or reset before answering.</summary>
    Unreachable = 1,

    /// <summary>The runtime rejected the configured credentials.</summary>
    AuthenticationRejected = 2,

    /// <summary>A configured timeout phase elapsed. The phase is carried alongside.</summary>
    Timeout = 3,

    /// <summary>The runtime answered, but not in a shape the protocol module can interpret.</summary>
    InvalidResponse = 4,

    /// <summary>The runtime's answer exceeded the configured body bound.</summary>
    ResponseTooLarge = 5,

    /// <summary>The caller cancelled before the runtime answered.</summary>
    Cancelled = 6,
}
