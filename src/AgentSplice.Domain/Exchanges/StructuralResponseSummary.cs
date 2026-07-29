namespace AgentSplice.Domain.Exchanges;

/// <summary>
/// A privacy-safe description of what a runtime returned (docs/SPECIFICATION.md FR-TRACE-003).
/// </summary>
/// <remarks>
/// Finish reasons are protocol tokens, not model output, so they are recorded verbatim.
/// <see cref="NativeToolCallCount"/> counts structured tool calls the runtime emitted as protocol
/// data; Stage 1 never derives it from prose, per FR-CHAT-014.
/// </remarks>
public sealed record StructuralResponseSummary
{
    /// <summary>Maximum number of recorded finish reasons.</summary>
    public const int MaxFinishReasons = 16;

    /// <summary>Maximum recorded length of a finish reason token.</summary>
    public const int MaxFinishReasonLength = 64;

    private StructuralResponseSummary()
    {
    }

    /// <summary>Number of choices in the response.</summary>
    public int ChoiceCount { get; private init; }

    /// <summary>Finish reason tokens observed, in first-seen order.</summary>
    public IReadOnlyList<string> FinishReasons { get; private init; } = [];

    /// <summary>Number of native structured tool calls observed as protocol data.</summary>
    public int NativeToolCallCount { get; private init; }

    /// <summary>Bytes forwarded to the client.</summary>
    public long ResponseBodyBytes { get; private init; }

    /// <summary>Complete SSE events forwarded to the client. Zero for a non-streamed exchange.</summary>
    public int StreamEventCount { get; private init; }

    /// <summary>True when the runtime reported a usage object.</summary>
    public bool UsageReported { get; private init; }

    /// <summary>Creates a validated structural response summary.</summary>
    public static StructuralResponseSummary Create(
        int choiceCount = 0,
        IEnumerable<string>? finishReasons = null,
        int nativeToolCallCount = 0,
        long responseBodyBytes = 0,
        int streamEventCount = 0,
        bool usageReported = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(choiceCount);
        ArgumentOutOfRangeException.ThrowIfNegative(nativeToolCallCount);
        ArgumentOutOfRangeException.ThrowIfNegative(responseBodyBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(streamEventCount);

        return new StructuralResponseSummary
        {
            ChoiceCount = choiceCount,
            FinishReasons = NormaliseFinishReasons(finishReasons),
            NativeToolCallCount = nativeToolCallCount,
            ResponseBodyBytes = responseBodyBytes,
            StreamEventCount = streamEventCount,
            UsageReported = usageReported,
        };
    }

    private static IReadOnlyList<string> NormaliseFinishReasons(IEnumerable<string>? finishReasons)
    {
        if (finishReasons is null)
        {
            return [];
        }

        var accumulated = new List<string>();

        foreach (var reason in finishReasons)
        {
            if (accumulated.Count == MaxFinishReasons)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                continue;
            }

            var trimmed = reason.Trim();
            var bounded = trimmed.Length <= MaxFinishReasonLength
                ? trimmed
                : trimmed[..MaxFinishReasonLength];

            if (!accumulated.Contains(bounded, StringComparer.Ordinal))
            {
                accumulated.Add(bounded);
            }
        }

        return accumulated.AsReadOnly();
    }
}
