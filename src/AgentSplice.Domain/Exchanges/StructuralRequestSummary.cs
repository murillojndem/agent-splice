using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace AgentSplice.Domain.Exchanges;

/// <summary>
/// A privacy-safe description of what a client sent, holding shapes and counts but never content
/// (docs/SPECIFICATION.md FR-TRACE-003, FR-TRACE-008, FR-CHAT-004).
/// </summary>
/// <remarks>
/// This type carries no message text, tool arguments, or field values. It records how many messages
/// there were, which roles appeared, how many tools were declared, and which unknown top-level
/// fields were present, so that "what did the client actually send" is answerable without storing
/// the prompt.
///
/// Every string it holds is either drawn from a closed vocabulary or hashed; see
/// <see cref="SafeVocabulary"/> for why bounding length and count was not enough. Both roles and
/// field names are chosen by the caller, and until this summary was persisted that only meant a
/// bounded amount of caller text in a trace attribute. Once it reaches a database with content
/// capture disabled, "bounded" and "safe" stop being the same claim.
/// </remarks>
public sealed record StructuralRequestSummary
{
    /// <summary>Maximum number of recorded unknown field names.</summary>
    public const int MaxUnknownFieldNames = 32;

    private StructuralRequestSummary()
    {
    }

    /// <summary>Number of messages in the request.</summary>
    public int MessageCount { get; private init; }

    /// <summary>
    /// Message counts per role, keyed by <see cref="SafeVocabulary.Roles"/>.
    /// </summary>
    /// <remarks>
    /// A role outside the vocabulary is counted under <see cref="SafeVocabulary.Unrecognised"/>, so
    /// the per-role counts still sum to <see cref="MessageCount"/> and a reader can tell that every
    /// message was accounted for.
    /// </remarks>
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
    /// Hashed names of top-level fields AgentSplice does not model, in first-seen order.
    /// </summary>
    /// <remarks>
    /// Recorded so that transparent forwarding of unknown fields is verifiable without storing a
    /// name the client chose. To ask whether a particular field appeared, hash it with
    /// <see cref="SafeVocabulary.HashName"/> and compare.
    /// </remarks>
    public IReadOnlyList<string> UnknownTopLevelFieldNames { get; private init; } = [];

    /// <summary>
    /// Hashed names of top-level fields that were not forwarded upstream. Empty in a transparent
    /// exchange; a non-empty list is the evidence FR-TRACE-008 requires for a structural difference.
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

    /// <summary>Creates a validated structural summary.</summary>
    /// <param name="unspecifiedRoleCount">
    /// Messages whose role was absent or was not a string, counted separately from
    /// <paramref name="messageCountsByRole"/> on purpose. A dictionary key is a string, and any string
    /// is one a client could have sent — so routing absence through the same channel would let
    /// <c>{"role": "(unspecified)"}</c> be recorded as a message that stated no role.
    /// </param>
    public static StructuralRequestSummary Create(
        int messageCount,
        IEnumerable<KeyValuePair<string, int>>? messageCountsByRole = null,
        int unspecifiedRoleCount = 0,
        int toolDeclarationCount = 0,
        bool toolChoicePresent = false,
        bool streamRequested = false,
        bool streamOptionsPresent = false,
        long requestBodyBytes = 0,
        IEnumerable<string>? unknownTopLevelFieldNames = null,
        IEnumerable<string>? droppedFieldNames = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(messageCount);
        ArgumentOutOfRangeException.ThrowIfNegative(unspecifiedRoleCount);
        ArgumentOutOfRangeException.ThrowIfNegative(toolDeclarationCount);
        ArgumentOutOfRangeException.ThrowIfNegative(requestBodyBytes);

        var unknownNames = HashNames(unknownTopLevelFieldNames);

        return new StructuralRequestSummary
        {
            MessageCount = messageCount,
            MessageCountsByRole = RoleCounts(messageCountsByRole, unspecifiedRoleCount),
            ToolDeclarationCount = toolDeclarationCount,
            ToolChoicePresent = toolChoicePresent,
            StreamRequested = streamRequested,
            StreamOptionsPresent = streamOptionsPresent,
            RequestBodyBytes = requestBodyBytes,
            UnknownTopLevelFieldNames = unknownNames.Names,
            UnknownFieldNamesTruncated = unknownNames.Truncated,
            DroppedFieldNames = HashNames(droppedFieldNames).Names,
        };
    }

    /// <summary>
    /// Counts messages per role, folding anything outside the vocabulary into one bucket.
    /// </summary>
    /// <remarks>
    /// Folding rather than dropping keeps <see cref="MessageCount"/> reconcilable with the sum of the
    /// per-role counts. There is no cardinality bound any more and none is needed: the vocabulary is
    /// closed, so the dictionary cannot exceed its size plus the two buckets however many distinct
    /// roles a request invents.
    /// </remarks>
    private static FrozenDictionary<string, int> RoleCounts(
        IEnumerable<KeyValuePair<string, int>>? roleCounts,
        int unspecifiedCount)
    {
        var accumulated = new Dictionary<string, int>(StringComparer.Ordinal);

        if (unspecifiedCount > 0)
        {
            // The one entry no caller-supplied string can reach.
            accumulated[SafeVocabulary.Unspecified] = unspecifiedCount;
        }

        foreach (var (role, count) in roleCounts ?? [])
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count, nameof(roleCounts));

            var name = SafeVocabulary.Role(role);
            accumulated[name] = accumulated.GetValueOrDefault(name) + count;
        }

        return accumulated.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Hashes each distinct name, preserving first-seen order and bounding the count.</summary>
    private static (ReadOnlyCollection<string> Names, bool Truncated) HashNames(IEnumerable<string>? names)
    {
        if (names is null)
        {
            return (ReadOnlyCollection<string>.Empty, false);
        }

        var accumulated = new List<string>();
        var truncated = false;

        foreach (var name in names)
        {
            var hashed = SafeVocabulary.HashName(name);

            if (accumulated.Contains(hashed, StringComparer.Ordinal))
            {
                continue;
            }

            if (accumulated.Count == MaxUnknownFieldNames)
            {
                // Bounded on purpose: hashing makes each name safe, and nothing makes an unbounded
                // number of them safe to accumulate per exchange.
                truncated = true;
                break;
            }

            accumulated.Add(hashed);
        }

        return (accumulated.AsReadOnly(), truncated);
    }
}
