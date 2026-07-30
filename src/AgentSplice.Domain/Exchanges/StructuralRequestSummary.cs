using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Globalization;

namespace AgentSplice.Domain.Exchanges;

/// <summary>
/// A privacy-safe description of what a client sent, holding shapes and counts but never content
/// (docs/SPECIFICATION.md FR-TRACE-003, FR-TRACE-008, FR-CHAT-004).
/// </summary>
/// <remarks>
/// This type carries no message text, tool arguments, or field values. It records how many messages
/// there were, which roles appeared, how many tools were declared, and which unknown top-level
/// fields were present by name, so that "what did the client actually send" is answerable without
/// storing the prompt. Field names are length-bounded and count-bounded for the same reason
/// <see cref="Observations.SafeDetails"/> is.
/// </remarks>
public sealed record StructuralRequestSummary
{
    /// <summary>Maximum number of recorded unknown field names.</summary>
    public const int MaxUnknownFieldNames = 32;

    /// <summary>Maximum recorded length of a single field name.</summary>
    public const int MaxFieldNameLength = 64;

    /// <summary>
    /// Maximum number of distinct role names counted individually.
    /// </summary>
    /// <remarks>
    /// Role names are client-supplied. Without a cardinality bound, a request carrying a unique
    /// role per message would grow this dictionary without limit, which is the very thing the
    /// per-name length bound exists to prevent.
    /// </remarks>
    public const int MaxRoleNames = 16;

    /// <summary>Bucket for roles beyond <see cref="MaxRoleNames"/>.</summary>
    public const string OtherRoleName = "(other)";

    /// <summary>Bucket for a message whose role was absent or not a string.</summary>
    public const string UnspecifiedRoleName = "(unspecified)";

    private StructuralRequestSummary()
    {
    }

    /// <summary>Number of messages in the request.</summary>
    public int MessageCount { get; private init; }

    /// <summary>Message counts per declared role. Role names only, never message content.</summary>
    public IReadOnlyDictionary<string, int> MessageCountsByRole { get; private init; } =
        FrozenDictionary<string, int>.Empty;

    /// <summary>Number of tool declarations forwarded transparently.</summary>
    public int ToolDeclarationCount { get; private init; }

    /// <summary>True when the request carried a <c>tool_choice</c> value.</summary>
    public bool ToolChoicePresent { get; private init; }

    /// <summary>True when the client requested a streamed response.</summary>
    public bool StreamRequested { get; private init; }

    /// <summary>True when the request carried <c>stream_options</c>.</summary>
    public bool StreamOptionsPresent { get; private init; }

    /// <summary>Size of the received request body in bytes.</summary>
    public long RequestBodyBytes { get; private init; }

    /// <summary>
    /// Names of top-level fields AgentSplice does not model. Recorded so that transparent
    /// forwarding of unknown fields is verifiable without inspecting their values.
    /// </summary>
    public IReadOnlyList<string> UnknownTopLevelFieldNames { get; private init; } = [];

    /// <summary>
    /// Names of top-level fields that were not forwarded upstream. Empty in a transparent exchange;
    /// a non-empty list is the evidence FR-TRACE-008 requires for a structural difference.
    /// </summary>
    public IReadOnlyList<string> DroppedFieldNames { get; private init; } = [];

    /// <summary>
    /// True when more unknown field names were present than <see cref="MaxUnknownFieldNames"/>.
    /// </summary>
    /// <remarks>
    /// Truncation has to be visible, or a reader cannot tell "there were exactly 32" from "there
    /// were at least 32", and a silent bound reads as completeness.
    /// </remarks>
    public bool UnknownFieldNamesTruncated { get; private init; }

    /// <summary>True when distinct roles exceeded <see cref="MaxRoleNames"/> and were folded.</summary>
    public bool RoleNamesTruncated { get; private init; }

    /// <summary>Creates a validated structural summary.</summary>
    public static StructuralRequestSummary Create(
        int messageCount,
        IEnumerable<KeyValuePair<string, int>>? messageCountsByRole = null,
        int toolDeclarationCount = 0,
        bool toolChoicePresent = false,
        bool streamRequested = false,
        bool streamOptionsPresent = false,
        long requestBodyBytes = 0,
        IEnumerable<string>? unknownTopLevelFieldNames = null,
        IEnumerable<string>? droppedFieldNames = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(messageCount);
        ArgumentOutOfRangeException.ThrowIfNegative(toolDeclarationCount);
        ArgumentOutOfRangeException.ThrowIfNegative(requestBodyBytes);

        var roles = NormaliseRoleCounts(messageCountsByRole);
        var unknownNames = NormaliseFieldNames(unknownTopLevelFieldNames, nameof(unknownTopLevelFieldNames));
        var droppedNames = NormaliseFieldNames(droppedFieldNames, nameof(droppedFieldNames));

        return new StructuralRequestSummary
        {
            MessageCount = messageCount,
            MessageCountsByRole = roles.Counts,
            RoleNamesTruncated = roles.Truncated,
            ToolDeclarationCount = toolDeclarationCount,
            ToolChoicePresent = toolChoicePresent,
            StreamRequested = streamRequested,
            StreamOptionsPresent = streamOptionsPresent,
            RequestBodyBytes = requestBodyBytes,
            UnknownTopLevelFieldNames = unknownNames.Names,
            UnknownFieldNamesTruncated = unknownNames.Truncated,
            DroppedFieldNames = droppedNames.Names,
        };
    }

    /// <summary>
    /// Counts messages per role, folding roles beyond the cardinality bound into a single bucket.
    /// </summary>
    /// <remarks>
    /// Folding rather than dropping keeps <see cref="MessageCount"/> reconcilable with the sum of
    /// the per-role counts, so a reader can still tell that every message was accounted for.
    /// </remarks>
    private static (FrozenDictionary<string, int> Counts, bool Truncated) NormaliseRoleCounts(
        IEnumerable<KeyValuePair<string, int>>? roleCounts)
    {
        if (roleCounts is null)
        {
            return (FrozenDictionary<string, int>.Empty, false);
        }

        var accumulated = new Dictionary<string, int>(StringComparer.Ordinal);
        var truncated = false;

        foreach (var (role, count) in roleCounts)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count, nameof(roleCounts));

            var name = TruncateName(role, nameof(roleCounts));

            if (!accumulated.ContainsKey(name) && accumulated.Count >= MaxRoleNames)
            {
                name = OtherRoleName;
                truncated = true;
            }

            accumulated[name] = accumulated.GetValueOrDefault(name) + count;
        }

        return (accumulated.ToFrozenDictionary(StringComparer.Ordinal), truncated);
    }

    private static (ReadOnlyCollection<string> Names, bool Truncated) NormaliseFieldNames(
        IEnumerable<string>? names,
        string parameterName)
    {
        if (names is null)
        {
            return (ReadOnlyCollection<string>.Empty, false);
        }

        var accumulated = new List<string>();
        var truncated = false;

        foreach (var name in names)
        {
            var candidate = TruncateName(name, parameterName);

            if (accumulated.Contains(candidate, StringComparer.Ordinal))
            {
                continue;
            }

            if (accumulated.Count == MaxUnknownFieldNames)
            {
                // Bounded on purpose: an adversarial or generated request could otherwise turn the
                // summary into an unbounded store of attacker-chosen strings.
                truncated = true;
                break;
            }

            accumulated.Add(candidate);
        }

        return (accumulated.AsReadOnly(), truncated);
    }

    private static string TruncateName(string? name, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, parameterName);

        var sanitised = name.Trim();

        foreach (var character in sanitised)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "'{0}' must not contain control characters.",
                        parameterName),
                    parameterName);
            }
        }

        return sanitised.Length <= MaxFieldNameLength
            ? sanitised
            : sanitised[..MaxFieldNameLength];
    }
}
