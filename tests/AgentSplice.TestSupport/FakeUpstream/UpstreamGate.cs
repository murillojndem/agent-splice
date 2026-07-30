namespace AgentSplice.TestSupport.FakeUpstream;

/// <summary>
/// A point in a scripted response the upstream stops at until a test releases it.
/// </summary>
/// <remarks>
/// Chunk delays are real wall-clock waits, so a test built on them either sleeps long enough to be
/// reliable or short enough to be fast, and never both. A gate replaces the guess with a fact: the
/// test waits until the upstream has demonstrably reached a point, does whatever it needs to observe
/// there — read the events delivered so far, disconnect the client, let an idle budget elapse — and
/// then lets the response continue.
///
/// This is what makes per-event delivery, mid-stream disconnect, and idle-timeout tests deterministic
/// rather than timing-dependent.
/// </remarks>
public sealed class UpstreamGate
{
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Lets the scripted response continue past this point.</summary>
    public void Release() => released.TrySetResult();

    /// <summary>Waits until the upstream has written everything up to this point and stopped.</summary>
    public Task WaitForReachedAsync(TimeSpan timeout) => reached.Task.WaitAsync(timeout);

    /// <summary>Called by the fixture when the response reaches this point.</summary>
    internal Task ArriveAsync(CancellationToken cancellationToken)
    {
        reached.TrySetResult();

        return released.Task.WaitAsync(cancellationToken);
    }
}
