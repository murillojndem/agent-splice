using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Application.Runtimes;

/// <summary>
/// A completion to forward to one runtime.
/// </summary>
/// <remarks>
/// Carries no credential. The provider resolves the runtime's key when it builds the request, so a
/// secret never travels through orchestration, never lands in an exchange record, and cannot be
/// interpolated into a log by any code that merely holds this value (docs/SECURITY.md).
///
/// <see cref="Body"/> is the exact bytes to send. Whether it is the client's original buffer or one
/// with the model identifier substituted has already been decided; the provider does not inspect or
/// re-encode it.
/// </remarks>
public sealed record ProviderCompletionRequest
{
    private ProviderCompletionRequest()
    {
    }

    /// <summary>The runtime to send to.</summary>
    public RuntimeTarget Target { get; private init; } = null!;

    /// <summary>The bytes to send, verbatim.</summary>
    public ReadOnlyMemory<byte> Body { get; private init; }

    /// <summary>The media type of <see cref="Body"/>.</summary>
    public string MediaType { get; private init; } = string.Empty;

    /// <summary>The media type to ask the runtime for.</summary>
    /// <remarks>
    /// Stated by the caller rather than assumed by the provider. Which media type a streamed
    /// response uses is a fact about the client-facing protocol, and a provider that guessed it
    /// would be encoding one protocol's rules into a module that is meant to serve any of them.
    /// </remarks>
    public string AcceptMediaType { get; private init; } = string.Empty;

    /// <summary>
    /// The correlation token to forward, so a runtime log line can be tied to an AgentSplice
    /// exchange. Carries no content.
    /// </summary>
    public PublicRequestId RequestId { get; private init; }

    /// <summary>Creates a provider request.</summary>
    public static ProviderCompletionRequest Create(
        RuntimeTarget target,
        ReadOnlyMemory<byte> body,
        string mediaType,
        string acceptMediaType,
        PublicRequestId requestId)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        ArgumentException.ThrowIfNullOrWhiteSpace(acceptMediaType);

        return new ProviderCompletionRequest
        {
            Target = target,
            Body = body,
            MediaType = mediaType,
            AcceptMediaType = acceptMediaType,
            RequestId = requestId,
        };
    }
}
