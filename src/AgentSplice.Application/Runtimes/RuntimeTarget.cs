using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Application.Runtimes;

/// <summary>
/// A configured runtime endpoint, projected from options into validated domain types
/// (docs/SPECIFICATION.md section 13.1).
/// </summary>
/// <remarks>
/// Carries no credential. The API key is named here and resolved by the provider at the moment it
/// builds the upstream request, so a secret never travels through routing, orchestration, or any
/// value that could be logged or recorded (docs/SECURITY.md).
/// </remarks>
public sealed record RuntimeTarget
{
    private RuntimeTarget()
    {
    }

    /// <summary>Stable identifier used in routing, traces, and metric dimensions.</summary>
    public RuntimeEndpointId Id { get; private init; }

    /// <summary>Provider adapter key, for example <c>lmstudio</c>.</summary>
    public string ProviderKey { get; private init; } = string.Empty;

    /// <summary>
    /// Base address of the runtime's OpenAI-compatible surface, always with a trailing slash so
    /// that a relative path resolves beneath it rather than replacing its last segment.
    /// </summary>
    public Uri BaseAddress { get; private init; } = null!;

    /// <summary>Name of the environment variable holding the API key. Never the key itself.</summary>
    public string? ApiKeyEnvironmentVariable { get; private init; }

    /// <summary>Whether this runtime participates in routing and discovery.</summary>
    public bool Enabled { get; private init; }

    /// <summary>Model discovery policy.</summary>
    public RuntimeDiscoveryPolicy Discovery { get; private init; } = null!;

    /// <summary>Timeout phases.</summary>
    public RuntimeTimeouts Timeouts { get; private init; } = null!;

    /// <summary>
    /// Position in <c>agentsplice:runtimes</c>. The deterministic tie-break when two runtimes offer
    /// the same upstream model identifier (FR-MOD-004, FR-MOD-005).
    /// </summary>
    public int Ordinal { get; private init; }

    /// <summary>Creates a validated runtime target.</summary>
    public static RuntimeTarget Create(
        RuntimeEndpointId id,
        string providerKey,
        Uri baseAddress,
        RuntimeDiscoveryPolicy discovery,
        RuntimeTimeouts timeouts,
        int ordinal,
        bool enabled = true,
        string? apiKeyEnvironmentVariable = null)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("A runtime target requires an identity.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(timeouts);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);

        if (!baseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("A runtime base address must be absolute.", nameof(baseAddress));
        }

        return new RuntimeTarget
        {
            Id = id,
            ProviderKey = providerKey.Trim(),
            BaseAddress = EnsureTrailingSlash(baseAddress),
            ApiKeyEnvironmentVariable = apiKeyEnvironmentVariable,
            Enabled = enabled,
            Discovery = discovery,
            Timeouts = timeouts,
            Ordinal = ordinal,
        };
    }

    /// <summary>Resolves a path relative to this runtime's OpenAI-compatible surface.</summary>
    public Uri ResolvePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        return new Uri(BaseAddress, relativePath.TrimStart('/'));
    }

    /// <summary>
    /// Appends a trailing slash when one is missing.
    /// </summary>
    /// <remarks>
    /// Without it, <c>new Uri("http://host:1234/v1", "models")</c> resolves to
    /// <c>http://host:1234/models</c>: the last segment is treated as a document name and replaced.
    /// Every configured LM Studio base URL ends in <c>/v1</c>, so the un-normalised form would send
    /// every discovery and completion request to the wrong path.
    /// </remarks>
    private static Uri EnsureTrailingSlash(Uri baseAddress) =>
        baseAddress.AbsoluteUri.EndsWith('/')
            ? baseAddress
            : new Uri(baseAddress.AbsoluteUri + "/", UriKind.Absolute);
}
