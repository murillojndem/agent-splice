namespace AgentSplice.Application.Runtimes;

/// <summary>
/// An upstream runtime's API key, wrapped so that revealing it is always a deliberate act
/// (docs/SPECIFICATION.md FR-DATA-010, docs/SECURITY.md "Upstream credentials").
/// </summary>
/// <remarks>
/// A bare <see cref="string"/> would be interpolated into a log template, an error message, or a
/// span attribute by any code path that happened to hold one, and each of those is a disclosure. The
/// wrapper makes the safe rendering the default: <see cref="ToString"/> never returns the value, so
/// an accidental <c>{Credential}</c> in a structured log emits a placeholder instead of a key.
///
/// Deliberately not a record: a generated <c>ToString</c> prints its members, which is exactly the
/// disclosure this type exists to prevent.
/// </remarks>
public sealed class RuntimeCredential
{
    /// <summary>Rendering used wherever a credential could otherwise be printed.</summary>
    public const string RedactedRendering = "[redacted]";

    /// <summary>Rendering used when no credential is configured.</summary>
    public const string AbsentRendering = "[none]";

    private readonly string? value;

    private RuntimeCredential(string? value) => this.value = value;

    /// <summary>No credential is configured for this runtime.</summary>
    /// <remarks>
    /// A first-class state rather than <c>null</c>, because a local runtime legitimately needs no
    /// key and "no credential" must not be indistinguishable from "the resolver was never called".
    /// </remarks>
    public static RuntimeCredential None { get; } = new(value: null);

    /// <summary>True when a credential is available to attach to an upstream request.</summary>
    public bool HasValue => value is not null;

    /// <summary>Wraps a resolved credential value.</summary>
    /// <remarks>
    /// A blank environment variable yields <see cref="None"/> rather than an empty credential: an
    /// empty bearer token would be sent upstream and rejected, reporting an authentication failure
    /// whose real cause is an unset variable.
    /// </remarks>
    public static RuntimeCredential FromValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? None : new RuntimeCredential(value);

    /// <summary>
    /// Returns the credential value. The single deliberate disclosure point, so that
    /// every place a key can escape is greppable.
    /// </summary>
    /// <exception cref="InvalidOperationException">No credential is configured.</exception>
    public string Reveal() =>
        value ?? throw new InvalidOperationException(
            "No credential is configured for this runtime; check HasValue before revealing.");

    /// <inheritdoc />
    public override string ToString() => HasValue ? RedactedRendering : AbsentRendering;
}
