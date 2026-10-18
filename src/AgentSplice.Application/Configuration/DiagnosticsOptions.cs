namespace AgentSplice.Application.Configuration;

/// <summary>
/// Diagnostic detail settings (docs/SPECIFICATION.md section 12, docs/SECURITY.md).
/// </summary>
/// <remarks>
/// Both are refused unless off (ADR 0027). No code in this build stores a body or retains a header,
/// so accepting either would tell an operator that something is kept which nothing keeps — the reason
/// <see cref="CaptureOptions.ContentEnabled"/> is refused. Each becomes a real setting in the slice
/// that implements what it names.
/// </remarks>
public sealed class DiagnosticsOptions
{
    /// <summary>
    /// Whether request and response bodies are stored with diagnostics. Must be false in this build.
    /// </summary>
    public bool StoreBodies { get; set; }

    /// <summary>
    /// How much header detail is retained. Must be <see cref="HeaderCaptureMode.None"/> in this build.
    /// </summary>
    public HeaderCaptureMode StoreHeaders { get; set; } = HeaderCaptureMode.None;
}
