namespace AgentSplice.Protocols.OpenAI.ChatCompletions;

/// <summary>Why a scan could not produce a usable envelope.</summary>
internal enum ScanFailure
{
    /// <summary>The scan succeeded.</summary>
    None = 0,

    /// <summary>The body is not valid JSON.</summary>
    MalformedJson,

    /// <summary>The body is valid JSON but not an object.</summary>
    NotAnObject,

    /// <summary><c>model</c> is absent, not a string, or empty.</summary>
    ModelMissing,

    /// <summary><c>model</c> is a string AgentSplice cannot carry.</summary>
    ModelInvalid,

    /// <summary><c>messages</c> is absent or not an array.</summary>
    MessagesMissing,

    /// <summary><c>messages</c> is an empty array.</summary>
    MessagesEmpty,

    /// <summary><c>stream</c> is present but not a boolean.</summary>
    StreamNotBoolean,

    /// <summary>A field whose value drives behaviour appears more than once.</summary>
    DuplicateField,
}

/// <summary>
/// The raw result of one pass over a completion request body.
/// </summary>
/// <remarks>
/// Deliberately a plain carrier of facts. Turning these into a validated envelope or an error is the
/// codec's job, so the scanning logic stays a single readable pass with no policy in it.
/// </remarks>
internal sealed class ChatCompletionRequestScan
{
    internal ScanFailure Failure { get; set; }

    /// <summary>The field a failure should name, when one can be named.</summary>
    internal string? FailedField { get; set; }

    internal string? Model { get; set; }

    /// <summary>Index of the opening quote of the top-level <c>model</c> value.</summary>
    internal int ModelValueStart { get; set; } = -1;

    /// <summary>Index one past the closing quote of the top-level <c>model</c> value.</summary>
    internal int ModelValueEnd { get; set; } = -1;

    internal bool StreamRequested { get; set; }

    internal bool StreamOptionsPresent { get; set; }

    internal bool ToolChoicePresent { get; set; }

    internal bool ToolsMalformed { get; set; }

    internal int ToolDeclarationCount { get; set; }

    internal int MessageCount { get; set; }

    internal Dictionary<string, int> RoleCounts { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Messages whose role was absent, null, or not a string.
    /// </summary>
    /// <remarks>
    /// Counted here rather than under a reserved key in <see cref="RoleCounts"/>, because a key is a
    /// string and a client can send any string it likes — including the reserved one.
    /// </remarks>
    internal int UnspecifiedRoleCount { get; set; }

    internal List<string> UnknownFieldNames { get; } = [];
}
