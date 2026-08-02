using AgentSplice.Domain.Measurements;

namespace AgentSplice.Infrastructure.Persistence;

/// <summary>
/// Token usage as it is written to the metadata store.
/// </summary>
/// <remarks>
/// A separate shape from <see cref="UsageObservation"/> because that type exposes
/// <see cref="TokenCount"/> as a <c>readonly record struct</c> whose provenance is an enum, and a
/// stored document should name what it holds rather than depend on the numeric value of an enum
/// member never changing.
///
/// Each component is independently optional and independently sourced. A runtime that reported
/// nothing yields <c>null</c> here rather than three zeroes: zero is a claim that no tokens were
/// consumed, absence is a claim that AgentSplice does not know (FR-OBS-003).
///
/// <see cref="TotalTokens"/> is stored as reported and never computed from the other two, because a
/// runtime may report a total that includes tokens AgentSplice cannot see.
/// </remarks>
internal sealed record PersistedUsage
{
    /// <summary>Prompt tokens, when known.</summary>
    public PersistedTokenCount? PromptTokens { get; init; }

    /// <summary>Completion tokens, when known.</summary>
    public PersistedTokenCount? CompletionTokens { get; init; }

    /// <summary>Total tokens as reported, when known.</summary>
    public PersistedTokenCount? TotalTokens { get; init; }

    /// <summary>Projects a usage observation, or returns <c>null</c> when no component is known.</summary>
    internal static PersistedUsage? From(UsageObservation usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        return usage.IsUnknown
            ? null
            : new PersistedUsage
            {
                PromptTokens = PersistedTokenCount.From(usage.PromptTokens),
                CompletionTokens = PersistedTokenCount.From(usage.CompletionTokens),
                TotalTokens = PersistedTokenCount.From(usage.TotalTokens),
            };
    }
}

/// <summary>A stored token count and the provenance it may never be separated from.</summary>
internal sealed record PersistedTokenCount(int Value, string Provenance)
{
    internal static PersistedTokenCount? From(TokenCount? tokens) =>
        tokens is { } count
            ? new PersistedTokenCount(count.Value, EnumWireNames.ToSnakeCase(count.Provenance))
            : null;
}
