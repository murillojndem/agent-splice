namespace AgentSplice.Application.Configuration;

/// <summary>
/// Diagnostic detail settings (docs/SPECIFICATION.md section 12, docs/SECURITY.md).
/// </summary>
public sealed class DiagnosticsOptions
{
    /// <summary>
    /// Whether request and response bodies may be attached to diagnostics. Off by default; content
    /// capture is governed by <see cref="CaptureOptions.ContentEnabled"/> as well, and both must be
    /// on for content to be stored.
    /// </summary>
    public bool StoreBodies { get; set; }

    /// <summary>How much header detail is retained.</summary>
    public HeaderCaptureMode StoreHeaders { get; set; } = HeaderCaptureMode.Allowlist;

    /// <summary>
    /// Headers retained when <see cref="StoreHeaders"/> is
    /// <see cref="HeaderCaptureMode.Allowlist"/>. The defaults are protocol and correlation headers
    /// only; nothing here can carry a credential.
    /// </summary>
    public IList<string> HeaderAllowlist { get; set; } = new List<string>
    {
        "content-type",
        "content-length",
        "accept",
        "user-agent",
        "x-request-id",
    };
}
