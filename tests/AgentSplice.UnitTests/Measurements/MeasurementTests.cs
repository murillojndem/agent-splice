using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Measurements;
using Xunit;

namespace AgentSplice.UnitTests.Measurements;

/// <summary>
/// Measurement guards. A measurement without provenance, or one carrying <c>NaN</c>, would be
/// indistinguishable from real evidence (docs/SPECIFICATION.md section 13.5, FR-OBS-003).
/// </summary>
public sealed class MeasurementTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Duration_records_milliseconds_as_a_measured_value()
    {
        var exchangeId = ExchangeId.New();

        var measurement = Measurement.Duration(
            MeasurementNames.UpstreamHeadersDuration,
            TimeSpan.FromMilliseconds(125),
            exchangeId);

        Assert.Equal(125d, measurement.Value);
        Assert.Equal(MeasurementUnit.Milliseconds, measurement.Unit);
        Assert.Equal(MeasurementProvenance.Measured, measurement.Provenance);
        Assert.Equal(exchangeId, measurement.ExchangeId);
        Assert.False(measurement.RequiresExplicitLabel);
    }

    [Fact]
    public void Duration_rejects_a_negative_interval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Measurement.Duration(
            MeasurementNames.TotalDuration,
            TimeSpan.FromMilliseconds(-1),
            ExchangeId.New()));
    }

    [Fact]
    public void Tokens_carries_the_provenance_of_the_count_rather_than_claiming_measurement()
    {
        var measurement = Measurement.Tokens(
            MeasurementNames.PromptTokens,
            TokenCount.FromGatewayEstimate(1200),
            ExchangeId.New());

        Assert.Equal(1200d, measurement.Value);
        Assert.Equal(MeasurementUnit.Tokens, measurement.Unit);
        Assert.Equal(MeasurementProvenance.Estimated, measurement.Provenance);
        Assert.True(measurement.RequiresExplicitLabel);
    }

    [Fact]
    public void Tokens_reports_an_upstream_count_as_upstream_reported()
    {
        var measurement = Measurement.Tokens(
            MeasurementNames.CompletionTokens,
            TokenCount.FromUpstream(64),
            ExchangeId.New());

        Assert.Equal(MeasurementProvenance.UpstreamReported, measurement.Provenance);
        Assert.False(measurement.RequiresExplicitLabel);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Create_rejects_non_finite_values_so_an_impossible_division_stays_absent(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Measurement.Create(
            MeasurementId.New(),
            MeasurementNames.GenerationThroughput,
            value,
            MeasurementUnit.TokensPerSecond,
            MeasurementProvenance.Measured));
    }

    [Fact]
    public void Create_rejects_an_interval_that_ends_before_it_starts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Measurement.Create(
            MeasurementId.New(),
            MeasurementNames.TotalDuration,
            10d,
            MeasurementUnit.Milliseconds,
            MeasurementProvenance.Measured,
            startedAt: Origin,
            endedAt: Origin.AddMilliseconds(-1)));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has-dash")]
    [InlineData("has/slash")]
    public void Create_rejects_names_that_would_widen_a_metric_dimension(string name)
    {
        Assert.Throws<ArgumentException>(() => Measurement.Create(
            MeasurementId.New(),
            name,
            1d,
            MeasurementUnit.Count,
            MeasurementProvenance.Measured));
    }

    [Fact]
    public void Create_rejects_an_empty_measurement_identity()
    {
        Assert.Throws<ArgumentException>(() => Measurement.Create(
            default,
            MeasurementNames.TotalDuration,
            1d,
            MeasurementUnit.Milliseconds,
            MeasurementProvenance.Measured));
    }

    [Fact]
    public void Count_and_Bytes_reject_negative_values()
    {
        var exchangeId = ExchangeId.New();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Measurement.Count(MeasurementNames.ClientStreamEvents, -1, exchangeId));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Measurement.Bytes(MeasurementNames.ClientResponseBytes, -1, exchangeId));
    }

    [Fact]
    public void Prompt_and_generation_throughput_use_distinct_names()
    {
        Assert.NotEqual(MeasurementNames.PromptThroughput, MeasurementNames.GenerationThroughput);
    }
}
