using System.Collections.ObjectModel;

namespace AgentSplice.Application.Runtimes;

/// <summary>
/// The outcome of asking one runtime for its model catalogue.
/// </summary>
/// <remarks>
/// A result rather than an exception, so that <c>AgentSplice.Application</c> never has to catch a
/// transport type and every discovery failure is classified by the module that understands the
/// transport. "Answered with nothing" and "could not be asked" are different outcomes here, because
/// resolution reports them as different failures (a 404 versus a 502).
/// </remarks>
public sealed record RuntimeModelListResult
{
    private RuntimeModelListResult()
    {
    }

    /// <summary>The catalogue, when the runtime answered. Empty is a valid answer.</summary>
    public IReadOnlyList<DiscoveredModel> Models { get; private init; } = [];

    /// <summary>Why the runtime could not be asked, or <c>null</c> when it answered.</summary>
    public UpstreamFailure? Failure { get; private init; }

    /// <summary>True when the runtime answered, whether or not it offered any model.</summary>
    public bool Succeeded => Failure is null;

    /// <summary>Records a catalogue the runtime actually returned.</summary>
    public static RuntimeModelListResult Success(IEnumerable<DiscoveredModel> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        return new RuntimeModelListResult
        {
            Models = new ReadOnlyCollection<DiscoveredModel>([.. models]),
        };
    }

    /// <summary>Records a classified discovery failure.</summary>
    public static RuntimeModelListResult Failed(UpstreamFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new RuntimeModelListResult { Failure = failure };
    }
}
