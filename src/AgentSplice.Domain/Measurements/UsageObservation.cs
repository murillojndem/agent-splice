namespace AgentSplice.Domain.Measurements;

/// <summary>
/// Token usage for one exchange, where every component is independently optional and independently
/// sourced (docs/SPECIFICATION.md FR-OBS-003, API compatibility rules in CLAUDE.md).
/// </summary>
/// <remarks>
/// A runtime that reports no usage yields <see cref="Unknown"/>, not zeroes. Zero is a claim that
/// no tokens were consumed; absence is a claim that AgentSplice does not know.
/// </remarks>
public sealed record UsageObservation
{
    private UsageObservation()
    {
    }

    /// <summary>Usage that was never reported or estimated.</summary>
    public static UsageObservation Unknown { get; } = new();

    /// <summary>Prompt tokens, when known.</summary>
    public TokenCount? PromptTokens { get; private init; }

    /// <summary>Completion tokens, when known.</summary>
    public TokenCount? CompletionTokens { get; private init; }

    /// <summary>
    /// Total tokens as reported, when known. Deliberately not computed from the two components: a
    /// runtime may report a total that includes tokens AgentSplice cannot see.
    /// </summary>
    public TokenCount? TotalTokens { get; private init; }

    /// <summary>True when no component is known.</summary>
    public bool IsUnknown => PromptTokens is null && CompletionTokens is null && TotalTokens is null;

    /// <summary>Creates a usage observation from independently sourced components.</summary>
    public static UsageObservation Create(
        TokenCount? promptTokens = null,
        TokenCount? completionTokens = null,
        TokenCount? totalTokens = null) =>
        promptTokens is null && completionTokens is null && totalTokens is null
            ? Unknown
            : new UsageObservation
            {
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                TotalTokens = totalTokens,
            };

    /// <summary>
    /// The weakest provenance across the known components, or <c>null</c> when nothing is known.
    /// </summary>
    public MeasurementProvenance? WeakestProvenance()
    {
        MeasurementProvenance? weakest = null;

        foreach (var component in new[] { PromptTokens, CompletionTokens, TotalTokens })
        {
            if (component is { } tokens)
            {
                weakest = weakest is null
                    ? tokens.Provenance
                    : MeasurementProvenanceRules.Combine(weakest.Value, tokens.Provenance);
            }
        }

        return weakest;
    }
}
