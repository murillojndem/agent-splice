using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Identifiers;

namespace AgentSplice.UnitTests.Models;

/// <summary>
/// A provider that answers from a script and counts how many times it was asked.
/// </summary>
/// <remarks>
/// The call count is the assertion target for the cache tests: "served from cache" is only
/// observable as the absence of an upstream call.
/// </remarks>
internal sealed class RecordingModelRuntimeProvider : IModelRuntimeProvider
{
    private readonly Queue<RuntimeModelListResult> scripted = new();
    private readonly Dictionary<string, RuntimeModelListResult> byRuntime = new(StringComparer.Ordinal);
    private RuntimeModelListResult standingAnswer =
        RuntimeModelListResult.Success([Model("qwen3.6-27b-mtp")]);

    public string ProviderKey => "lmstudio";

    /// <summary>How many times a runtime was actually contacted.</summary>
    public int CallCount { get; private set; }

    /// <summary>How many times each runtime was contacted, keyed by runtime identifier.</summary>
    public Dictionary<string, int> CallsByRuntime { get; } = new(StringComparer.Ordinal);

    /// <summary>Gives one runtime its own standing answer.</summary>
    internal RecordingModelRuntimeProvider ForRuntime(string runtimeId, RuntimeModelListResult result)
    {
        byRuntime[runtimeId] = result;
        return this;
    }

    /// <summary>Makes one runtime start failing, so recovery and stale-serving can be exercised.</summary>
    internal RecordingModelRuntimeProvider Fails(
        string runtimeId,
        UpstreamFailureReason reason = UpstreamFailureReason.Unreachable) =>
        ForRuntime(runtimeId, RuntimeModelListResult.Failed(UpstreamFailure.Create(reason)));

    /// <summary>Released when <see cref="Gate"/> is set, so concurrency can be forced deterministically.</summary>
    public TaskCompletionSource? Gate { get; set; }

    internal static DiscoveredModel Model(string id, long? created = null, string? ownedBy = null) =>
        DiscoveredModel.Create(UpstreamModelId.Create(id), created, ownedBy);

    internal RecordingModelRuntimeProvider Answers(RuntimeModelListResult result)
    {
        standingAnswer = result;
        return this;
    }

    internal RecordingModelRuntimeProvider ThenAnswers(RuntimeModelListResult result)
    {
        scripted.Enqueue(result);
        return this;
    }

    /// <summary>The completion requests this provider was asked to forward, in order.</summary>
    public List<ProviderCompletionRequest> ForwardedCompletions { get; } = [];

    /// <summary>What the next completion call answers with.</summary>
    public Func<ProviderCompletionRequest, ProviderCompletionResult>? CompletionAnswer { get; set; }

    /// <summary>What the next streaming call answers with.</summary>
    public Func<ProviderCompletionRequest, ProviderStreamResult>? StreamAnswer { get; set; }

    public Task<ProviderStreamResult> StreamAsync(
        ProviderCompletionRequest request,
        CancellationToken cancellationToken)
    {
        ForwardedCompletions.Add(request);

        var answer = StreamAnswer
            ?? throw new InvalidOperationException(
                "No stream answer was scripted; a test that forgot to script one should fail loudly.");

        return Task.FromResult(answer(request));
    }

    public Task<ProviderCompletionResult> CompleteAsync(
        ProviderCompletionRequest request,
        CancellationToken cancellationToken)
    {
        ForwardedCompletions.Add(request);

        var answer = CompletionAnswer
            ?? throw new InvalidOperationException(
                "No completion answer was scripted; a test that forgot to script one should fail loudly.");

        return Task.FromResult(answer(request));
    }

    public async Task<RuntimeModelListResult> ListModelsAsync(
        RuntimeTarget target,
        CancellationToken cancellationToken)
    {
        CallCount++;
        CallsByRuntime[target.Id.Value] = CallsByRuntime.GetValueOrDefault(target.Id.Value) + 1;

        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (byRuntime.TryGetValue(target.Id.Value, out var scoped))
        {
            return scoped;
        }

        return scripted.Count > 0 ? scripted.Dequeue() : standingAnswer;
    }
}
