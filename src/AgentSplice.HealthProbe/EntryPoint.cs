namespace AgentSplice.HealthProbe;

/// <summary>
/// The command Docker runs as the image's <c>HEALTHCHECK</c> (ADR 0016).
/// </summary>
/// <remarks>
/// An explicit entry point rather than top-level statements: those generate a global
/// <c>Program</c> type, and a test project that can see both this assembly and the gateway's would
/// then have two of them.
/// </remarks>
internal static class EntryPoint
{
    private static async Task<int> Main(string[] args)
    {
        using var handler = LivenessProbe.CreateHandler();

        return await LivenessProbe.RunAsync(args, Console.Out, TimeProvider.System, handler).ConfigureAwait(false);
    }
}
