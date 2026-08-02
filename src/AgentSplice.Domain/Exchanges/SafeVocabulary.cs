using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;

namespace AgentSplice.Domain.Exchanges;

/// <summary>
/// Keeps caller-chosen text out of the structural summaries.
/// </summary>
/// <remarks>
/// A structural summary is the one thing AgentSplice retains when content capture is off, so every
/// value it holds has to be safe to store, export, and show on a dashboard without an authorization
/// decision (FR-DATA-005, FR-TRACE-003, docs/SECURITY.md "Safe structural summaries must not
/// reconstruct sensitive payloads accidentally").
///
/// Bounding length and count is not enough, and that was the defect this type exists to close. A
/// client picks the value of <c>role</c> and the name of any JSON property it likes; a runtime picks
/// the value of <c>finish_reason</c>. Truncating those to 64 characters bounds how much
/// caller-chosen text is stored and does nothing at all about the fact that it is caller-chosen.
/// A prompt fragment in <c>"role"</c> reached SQLite with content capture disabled.
///
/// Two rules, chosen by what the value is:
///
/// <list type="bullet">
/// <item>A value drawn from a protocol's own vocabulary — a role, a finish reason — is matched
/// against that vocabulary and replaced by a bucket when it does not match. The vocabulary is closed,
/// so no caller-chosen text survives, and the count stays reconcilable.</item>
/// <item>A value with no vocabulary — the name of an unknown request field — is hashed. There is
/// nothing to match it against, and the reason it is recorded at all is to make transparent
/// forwarding verifiable, which a stable identifier satisfies as well as the name does. An operator
/// asking "was <c>top_k</c> forwarded?" hashes <c>top_k</c> and compares.</item>
/// </list>
/// </remarks>
public static class SafeVocabulary
{
    /// <summary>
    /// Bucket for a value the caller sent that its protocol's vocabulary does not contain.
    /// </summary>
    /// <remarks>
    /// Covers every non-canonical spelling, not only hostile ones: <c>"User"</c>, <c>" user "</c>, and
    /// <c>""</c> all land here, because each is a thing the caller sent and none is the token the
    /// protocol defines.
    /// </remarks>
    public const string Unrecognised = "(unrecognised)";

    /// <summary>
    /// Bucket for a field that was absent, or present and not a string.
    /// </summary>
    /// <remarks>
    /// Reachable only from a <c>null</c>, which is to say only from AgentSplice observing an absence.
    /// No caller-supplied string produces it, including the literal <c>"(unspecified)"</c>.
    /// </remarks>
    public const string Unspecified = "(unspecified)";

    /// <summary>Prefix identifying a hashed name, so a reader is never left guessing what it is.</summary>
    public const string HashPrefix = "sha256-";

    /// <summary>Hex characters of the digest retained.</summary>
    /// <remarks>
    /// Enough that two distinct names colliding is not a practical concern for the number of field
    /// names one deployment sees, and short enough to read. The digest is not a secret and is not
    /// treated as one: it identifies a name across exchanges and is never reversed.
    /// </remarks>
    public const int HashLength = 12;

    /// <summary>
    /// Message roles recorded under their own name.
    /// </summary>
    /// <remarks>
    /// The OpenAI Chat Completions vocabulary, and deliberately not extended with whatever a client
    /// happens to send. A deployment using a role outside it loses the name and keeps the count,
    /// which is the correct trade when the alternative is storing arbitrary client text.
    /// </remarks>
    public static FrozenSet<string> Roles { get; } = new[]
    {
        "system",
        "user",
        "assistant",
        "tool",
        "function",
        "developer",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Finish reasons recorded under their own name.</summary>
    /// <remarks>
    /// From the runtime rather than the client, which makes it a smaller threat and not a different
    /// one: docs/THREAT_MODEL.md treats a malicious upstream as in scope, and a runtime that returns
    /// a paragraph of generated text as its finish reason would otherwise have that paragraph stored.
    /// A runtime with a genuinely new finish reason is recorded as <see cref="Unrecognised"/>; Stage 2
    /// conformance is where a novel token is worth capturing deliberately, under a policy, rather than
    /// by default.
    /// </remarks>
    public static FrozenSet<string> FinishReasons { get; } = new[]
    {
        "stop",
        "length",
        "tool_calls",
        "content_filter",
        "function_call",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Returns the role itself when the vocabulary contains it exactly, and a bucket otherwise.</summary>
    public static string Role(string? value) => Match(value, Roles);

    /// <summary>Returns the finish reason itself when the vocabulary contains it exactly, and a bucket otherwise.</summary>
    public static string FinishReason(string? value) => Match(value, FinishReasons);

    /// <summary>
    /// Returns a stable identifier for a caller-chosen name.
    /// </summary>
    /// <remarks>
    /// Never throws, whatever arrives. Client input reaching a validating helper that rejects control
    /// characters by throwing turns a hostile <c>role</c> into a failed request, which is a denial of
    /// service wearing the costume of input validation.
    /// </remarks>
    public static string HashName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Unspecified;
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));

        return string.Concat(HashPrefix, Convert.ToHexString(digest)[..HashLength].ToLowerInvariant());
    }

    /// <summary>
    /// Exact, ordinal, and unforgiving.
    /// </summary>
    /// <remarks>
    /// An earlier version trimmed and lower-cased before matching, so <c>" User "</c> was recorded as
    /// <c>user</c> — a token the client never sent. That is a semantic transformation of the observed
    /// protocol, stored as though it were the observation, in a product whose entire claim is that it
    /// reports what actually crossed the wire. A client that sends a non-canonical role has done
    /// something worth seeing, and <see cref="Unrecognised"/> is what says so.
    ///
    /// The buckets are outputs and are never accepted as inputs. Passing them through made
    /// <c>{"role": "(unspecified)"}</c> indistinguishable from a message that stated no role at all,
    /// which let a client forge an AgentSplice-internal observation. Absence now travels out of band —
    /// as a C# <c>null</c> here, and as a separate count in
    /// <see cref="StructuralRequestSummary.Create"/> — because a dictionary key is a string and any
    /// string is forgeable, while the absence of one is not.
    /// </remarks>
    private static string Match(string? value, FrozenSet<string> vocabulary) =>
        value is null ? Unspecified
            : vocabulary.Contains(value) ? value
            : Unrecognised;
}
