using AgentSplice.Domain.Exchanges;

namespace AgentSplice.Application.Runtimes;

/// <summary>
/// What a runtime answered, or why it could not be asked.
/// </summary>
/// <remarks>
/// A non-2xx answer is a success here. The runtime replied, and its reply — status, body, and the
/// headers on the response allowlist — is relayed verbatim. Only a transport failure, a timeout, or
/// a body that could not be read at all produces a <see cref="Failure"/>.
///
/// The timing fields exist so that <c>UpstreamHeadersReceived</c>, <c>FirstUpstreamByte</c>, and
/// <c>UpstreamCompleted</c> are boundaries the provider actually observed rather than moments the
/// orchestrator inferred after the fact (FR-TRACE-006).
/// </remarks>
public sealed record ProviderCompletionResult
{
    private ProviderCompletionResult()
    {
    }

    /// <summary>What the response headers said, or <c>null</c> when none arrived.</summary>
    public UpstreamResponseMetadata? Response { get; private init; }

    /// <summary>The body exactly as received.</summary>
    public ReadOnlyMemory<byte> Body { get; private init; }

    /// <summary>Why the runtime could not be asked, or <c>null</c> when it answered.</summary>
    public UpstreamFailure? Failure { get; private init; }

    /// <summary>When the first body byte arrived, or <c>null</c> when none did.</summary>
    public DateTimeOffset? FirstByteAt { get; private init; }

    /// <summary>When the body finished arriving, or <c>null</c> when it never did.</summary>
    public DateTimeOffset? CompletedAt { get; private init; }

    /// <summary>Headers from the response allowlist, to be relayed to the client.</summary>
    public IReadOnlyDictionary<string, string> RelayedHeaders { get; private init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>What establishing a connection cost, or <c>null</c> when a pooled one was reused.</summary>
    public UpstreamConnectObservation? Connection { get; private init; }

    /// <summary>Attaches connection timing to a result.</summary>
    public ProviderCompletionResult WithConnection(UpstreamConnectObservation? connection) =>
        connection is null ? this : this with { Connection = connection };

    /// <summary>True when the runtime answered, whatever status it chose.</summary>
    public bool Answered => Response is not null && Failure is null;

    /// <summary>Records an answer, whatever its status.</summary>
    public static ProviderCompletionResult FromResponse(
        UpstreamResponseMetadata response,
        ReadOnlyMemory<byte> body,
        DateTimeOffset? firstByteAt,
        DateTimeOffset completedAt,
        IReadOnlyDictionary<string, string>? relayedHeaders = null)
    {
        ArgumentNullException.ThrowIfNull(response);

        return new ProviderCompletionResult
        {
            Response = response,
            Body = body,
            FirstByteAt = firstByteAt,
            CompletedAt = completedAt,
            RelayedHeaders = relayedHeaders
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>Records that the runtime could not be asked, or could not finish answering.</summary>
    public static ProviderCompletionResult Failed(
        UpstreamFailure failure,
        UpstreamResponseMetadata? response = null,
        DateTimeOffset? firstByteAt = null)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new ProviderCompletionResult
        {
            Failure = failure,
            Response = response,
            FirstByteAt = firstByteAt,
        };
    }
}
