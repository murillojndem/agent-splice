namespace AgentSplice.Domain.Measurements;

/// <summary>
/// How provenance propagates through derived measurements.
/// </summary>
public static class MeasurementProvenanceRules
{
    /// <summary>
    /// Combines the provenance of several inputs into the provenance of a derived value.
    /// </summary>
    /// <remarks>
    /// The result is the weakest input. A generation-throughput value computed from a measured
    /// duration and an estimated token count is an estimate, not a measurement; reporting it as
    /// measured is precisely the false-precision failure CLAUDE.md forbids.
    /// </remarks>
    public static MeasurementProvenance Combine(MeasurementProvenance first, MeasurementProvenance second) =>
        first > second ? first : second;

    /// <summary>Combines the provenance of an arbitrary number of inputs.</summary>
    /// <exception cref="ArgumentException"><paramref name="provenances"/> is empty.</exception>
    public static MeasurementProvenance Combine(IEnumerable<MeasurementProvenance> provenances)
    {
        ArgumentNullException.ThrowIfNull(provenances);

        MeasurementProvenance? weakest = null;

        foreach (var provenance in provenances)
        {
            weakest = weakest is null ? provenance : Combine(weakest.Value, provenance);
        }

        return weakest
            ?? throw new ArgumentException(
                "A derived measurement requires at least one input provenance.",
                nameof(provenances));
    }

    /// <summary>
    /// True when the value is not a direct AgentSplice observation and must therefore be labelled
    /// as estimated or inferred wherever it is displayed (FR-OBS-010).
    /// </summary>
    public static bool RequiresExplicitLabel(MeasurementProvenance provenance) =>
        provenance is MeasurementProvenance.Estimated or MeasurementProvenance.Inferred;
}
