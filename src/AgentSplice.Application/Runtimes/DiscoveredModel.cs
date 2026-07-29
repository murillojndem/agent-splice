using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Application.Runtimes;

/// <summary>
/// One model reported by a runtime's own catalogue (docs/SPECIFICATION.md FR-MOD-002).
/// </summary>
/// <remarks>
/// <see cref="Created"/> is nullable on purpose. It is a Unix timestamp, so zero is a claim that the
/// model was created on 1970-01-01 rather than a statement that AgentSplice does not know
/// (FR-TRACE-006, FR-DASH-006). A runtime that reports no creation time yields <c>null</c>, and only
/// the OpenAI-compatible envelope substitutes a sentinel, because that schema requires an integer.
/// </remarks>
public sealed record DiscoveredModel
{
    /// <summary>Maximum retained length of an owner string.</summary>
    public const int MaxOwnedByLength = 128;

    private DiscoveredModel()
    {
    }

    /// <summary>The identifier the runtime answers to.</summary>
    public UpstreamModelId Id { get; private init; }

    /// <summary>Creation time as the runtime reported it, or <c>null</c> when it reported none.</summary>
    public long? Created { get; private init; }

    /// <summary>Owner as the runtime reported it, or <c>null</c> when it reported none.</summary>
    public string? OwnedBy { get; private init; }

    /// <summary>Creates a validated discovered model.</summary>
    public static DiscoveredModel Create(UpstreamModelId id, long? created = null, string? ownedBy = null)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("A discovered model requires an identifier.", nameof(id));
        }

        return new DiscoveredModel
        {
            Id = id,
            Created = created,
            OwnedBy = NormaliseOwnedBy(ownedBy),
        };
    }

    /// <summary>
    /// Bounds and sanitises an owner string.
    /// </summary>
    /// <remarks>
    /// The value is chosen by the runtime, so it is untrusted text on the same footing as a model
    /// identifier: it is echoed in <c>GET /v1/models</c> and could otherwise carry control
    /// characters or unbounded length into a client response.
    /// </remarks>
    private static string? NormaliseOwnedBy(string? ownedBy)
    {
        if (string.IsNullOrWhiteSpace(ownedBy))
        {
            return null;
        }

        var trimmed = ownedBy.Trim();
        var builder = new System.Text.StringBuilder(Math.Min(trimmed.Length, MaxOwnedByLength));

        foreach (var character in trimmed)
        {
            if (builder.Length == MaxOwnedByLength)
            {
                break;
            }

            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString();
    }
}
