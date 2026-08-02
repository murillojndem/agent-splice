using System.Collections.ObjectModel;

namespace AgentSplice.Domain.Exchanges;

/// <summary>
/// A privacy-safe description of what a runtime returned (docs/SPECIFICATION.md FR-TRACE-003).
/// </summary>
/// <remarks>
/// Finish reasons are protocol tokens rather than model output, but the runtime chooses the string,
/// so they are matched against <see cref="SafeVocabulary.FinishReasons"/> rather than recorded
/// verbatim. A runtime that returned generated text in that field would otherwise have it stored with
/// content capture disabled, and docs/THREAT_MODEL.md treats a malicious upstream as in scope.
///
/// <see cref="NativeToolCallCount"/> counts structured tool calls the runtime emitted as protocol
/// data; Stage 1 never derives it from prose, per FR-CHAT-014.
/// </remarks>
public sealed record StructuralResponseSummary
{
    private StructuralResponseSummary()
    {
    }

    /// <summary>Number of choices in the response.</summary>
    public int ChoiceCount { get; private init; }

    /// <summary>
    /// Finish reasons observed, in first-seen order, keyed by <see cref="SafeVocabulary.FinishReasons"/>.
    /// </summary>
    /// <remarks>
    /// A reason outside the vocabulary appears as <see cref="SafeVocabulary.Unrecognised"/>. That the
    /// runtime returned something unexpected stays visible; what it returned does not.
    /// </remarks>
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

    /// <summary>
    /// Maps each reason onto the vocabulary, keeping first-seen order.
    /// </summary>
    /// <remarks>
    /// No count bound and none needed: the vocabulary is closed, so the list cannot exceed its size
    /// plus one bucket however many distinct strings a runtime returns. The bound and its truncation
    /// flag were removed rather than kept as unreachable contracts.
    /// </remarks>
    private static ReadOnlyCollection<string> NormaliseFinishReasons(IEnumerable<string>? finishReasons)
    {
        if (finishReasons is null)
        {
            return ReadOnlyCollection<string>.Empty;
        }

        var accumulated = new List<string>();

        foreach (var reason in finishReasons)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                continue;
            }

            var recognised = SafeVocabulary.FinishReason(reason);

            if (!accumulated.Contains(recognised, StringComparer.Ordinal))
            {
                accumulated.Add(recognised);
            }
        }

        return accumulated.AsReadOnly();
    }
}
