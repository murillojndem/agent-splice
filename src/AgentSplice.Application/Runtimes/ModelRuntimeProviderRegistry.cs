using System.Collections.Frozen;

namespace AgentSplice.Application.Runtimes;

/// <summary>
/// Maps a runtime's configured <c>provider</c> key to the module that speaks its protocol.
/// </summary>
/// <remarks>
/// A runtime naming a provider no module implements is a configuration defect, not an availability
/// problem, so lookup failure is reported to the caller rather than swallowed: the two produce
/// different client-facing errors.
/// </remarks>
public sealed class ModelRuntimeProviderRegistry
{
    private readonly FrozenDictionary<string, IModelRuntimeProvider> byKey;

    /// <summary>Indexes the registered providers by their key.</summary>
    /// <exception cref="InvalidOperationException">Two providers claim the same key.</exception>
    public ModelRuntimeProviderRegistry(IEnumerable<IModelRuntimeProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var accumulated = new Dictionary<string, IModelRuntimeProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in providers)
        {
            if (!accumulated.TryAdd(provider.ProviderKey, provider))
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant(
                        $"Two provider modules claim the key '{provider.ProviderKey}'; routing would depend on registration order."));
            }
        }

        byKey = accumulated.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Finds the provider serving a runtime, or <c>null</c> when no module implements its key.</summary>
    public IModelRuntimeProvider? Find(RuntimeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return byKey.TryGetValue(target.ProviderKey, out var provider) ? provider : null;
    }
}
