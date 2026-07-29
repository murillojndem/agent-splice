using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Domain.Measurements;

/// <summary>
/// Derives tokens-per-second measurements, and refuses to derive them when the evidence is
/// insufficient (docs/SPECIFICATION.md FR-OBS-004, FR-OBS-005, section 15.3).
/// </summary>
/// <remarks>
/// Every method returns <c>null</c> rather than a zero or a partially-founded number. Prompt and
/// generation throughput have separate entry points so that a caller cannot accidentally publish
/// prompt-processing speed under the generation metric name.
/// </remarks>
public static class ThroughputCalculator
{
    /// <summary>
    /// Prompt-processing throughput, or <c>null</c> when the token count or prompt-processing
    /// duration is unknown or non-positive.
    /// </summary>
    public static Measurement? TryCalculatePromptThroughput(
        TokenCount? promptTokens,
        TimeSpan? promptDuration,
        ExchangeId exchangeId,
        double? confidence = null) =>
        TryCalculate(MeasurementNames.PromptThroughput, promptTokens, promptDuration, exchangeId, confidence);

    /// <summary>
    /// Generation throughput, or <c>null</c> when the token count or generation duration is unknown
    /// or non-positive.
    /// </summary>
    public static Measurement? TryCalculateGenerationThroughput(
        TokenCount? completionTokens,
        TimeSpan? generationDuration,
        ExchangeId exchangeId,
        double? confidence = null) =>
        TryCalculate(MeasurementNames.GenerationThroughput, completionTokens, generationDuration, exchangeId, confidence);

    private static Measurement? TryCalculate(
        string name,
        TokenCount? tokens,
        TimeSpan? duration,
        ExchangeId exchangeId,
        double? confidence)
    {
        if (tokens is not { } tokenCount || duration is not { } elapsed)
        {
            return null;
        }

        if (elapsed <= TimeSpan.Zero)
        {
            // A non-positive interval is not evidence of infinite throughput.
            return null;
        }

        if (tokenCount.Value == 0)
        {
            // Zero tokens over a real interval is a legitimate zero rate, but it says nothing about
            // throughput and would drag any aggregate down, so it stays unknown.
            return null;
        }

        var tokensPerSecond = tokenCount.Value / elapsed.TotalSeconds;

        if (!double.IsFinite(tokensPerSecond))
        {
            return null;
        }

        // The duration is measured but the token count may not be. A derived value can never be
        // stronger than its weakest input.
        var provenance = MeasurementProvenanceRules.Combine(
            MeasurementProvenance.Measured,
            tokenCount.Provenance);

        return Measurement.Create(
            MeasurementId.New(),
            name,
            tokensPerSecond,
            MeasurementUnit.TokensPerSecond,
            provenance,
            exchangeId,
            confidence);
    }
}
