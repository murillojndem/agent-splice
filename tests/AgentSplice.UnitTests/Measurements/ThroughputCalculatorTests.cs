using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Measurements;
using Xunit;

namespace AgentSplice.UnitTests.Measurements;

/// <summary>
/// Throughput derivation. FR-OBS-004 allows a throughput value only when the evidence supports it,
/// and FR-OBS-005 keeps prompt and generation rates apart. Every "insufficient evidence" case here
/// must yield <c>null</c>, never zero.
/// </summary>
public sealed class ThroughputCalculatorTests
{
    [Fact]
    public void Generation_throughput_is_unknown_when_the_token_count_is_unknown()
    {
        Assert.Null(ThroughputCalculator.TryCalculateGenerationThroughput(
            completionTokens: null,
            generationDuration: TimeSpan.FromSeconds(2),
            ExchangeId.New()));
    }

    [Fact]
    public void Generation_throughput_is_unknown_when_the_duration_is_unknown()
    {
        Assert.Null(ThroughputCalculator.TryCalculateGenerationThroughput(
            TokenCount.FromUpstream(100),
            generationDuration: null,
            ExchangeId.New()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Generation_throughput_is_unknown_for_a_non_positive_interval(int milliseconds)
    {
        Assert.Null(ThroughputCalculator.TryCalculateGenerationThroughput(
            TokenCount.FromUpstream(100),
            TimeSpan.FromMilliseconds(milliseconds),
            ExchangeId.New()));
    }

    [Fact]
    public void Generation_throughput_is_unknown_for_a_zero_token_count()
    {
        Assert.Null(ThroughputCalculator.TryCalculateGenerationThroughput(
            TokenCount.FromUpstream(0),
            TimeSpan.FromSeconds(1),
            ExchangeId.New()));
    }

    [Fact]
    public void Generation_throughput_uses_the_generation_measurement_name()
    {
        var measurement = ThroughputCalculator.TryCalculateGenerationThroughput(
            TokenCount.FromUpstream(120),
            TimeSpan.FromSeconds(2),
            ExchangeId.New());

        Assert.NotNull(measurement);
        Assert.Equal(MeasurementNames.GenerationThroughput, measurement.Name);
        Assert.Equal(60d, measurement.Value);
        Assert.Equal(MeasurementUnit.TokensPerSecond, measurement.Unit);
    }

    [Fact]
    public void Prompt_throughput_uses_the_prompt_measurement_name()
    {
        var measurement = ThroughputCalculator.TryCalculatePromptThroughput(
            TokenCount.FromUpstream(500),
            TimeSpan.FromSeconds(2),
            ExchangeId.New());

        Assert.NotNull(measurement);
        Assert.Equal(MeasurementNames.PromptThroughput, measurement.Name);
        Assert.Equal(250d, measurement.Value);
    }

    [Fact]
    public void A_measured_interval_combined_with_an_estimated_count_yields_an_estimate()
    {
        var measurement = ThroughputCalculator.TryCalculateGenerationThroughput(
            TokenCount.FromGatewayEstimate(120),
            TimeSpan.FromSeconds(2),
            ExchangeId.New());

        Assert.NotNull(measurement);
        Assert.Equal(MeasurementProvenance.Estimated, measurement.Provenance);
        Assert.True(measurement.RequiresExplicitLabel);
    }

    [Fact]
    public void An_upstream_reported_count_over_a_measured_interval_stays_upstream_reported()
    {
        var measurement = ThroughputCalculator.TryCalculateGenerationThroughput(
            TokenCount.FromUpstream(120),
            TimeSpan.FromSeconds(2),
            ExchangeId.New());

        Assert.NotNull(measurement);
        Assert.Equal(MeasurementProvenance.UpstreamReported, measurement.Provenance);
    }
}
