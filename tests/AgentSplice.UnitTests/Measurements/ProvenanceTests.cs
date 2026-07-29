using AgentSplice.Domain.Measurements;
using Xunit;

namespace AgentSplice.UnitTests.Measurements;

/// <summary>
/// Provenance propagation and usage absence rules
/// (docs/SPECIFICATION.md FR-OBS-003, FR-OBS-010, section 15.3).
/// </summary>
public sealed class ProvenanceTests
{
    [Theory]
    [InlineData(MeasurementProvenance.Measured, MeasurementProvenance.Estimated, MeasurementProvenance.Estimated)]
    [InlineData(MeasurementProvenance.Measured, MeasurementProvenance.UpstreamReported, MeasurementProvenance.UpstreamReported)]
    [InlineData(MeasurementProvenance.Estimated, MeasurementProvenance.Inferred, MeasurementProvenance.Inferred)]
    [InlineData(MeasurementProvenance.Measured, MeasurementProvenance.Measured, MeasurementProvenance.Measured)]
    [InlineData(MeasurementProvenance.RuntimeLog, MeasurementProvenance.ClientReported, MeasurementProvenance.ClientReported)]
    public void Combine_returns_the_weakest_input(
        MeasurementProvenance first,
        MeasurementProvenance second,
        MeasurementProvenance expected)
    {
        Assert.Equal(expected, MeasurementProvenanceRules.Combine(first, second));
        Assert.Equal(expected, MeasurementProvenanceRules.Combine(second, first));
    }

    [Fact]
    public void Combine_over_a_sequence_returns_the_weakest_member()
    {
        var combined = MeasurementProvenanceRules.Combine(
        [
            MeasurementProvenance.Measured,
            MeasurementProvenance.UpstreamReported,
            MeasurementProvenance.Inferred,
        ]);

        Assert.Equal(MeasurementProvenance.Inferred, combined);
    }

    [Fact]
    public void Combine_rejects_an_empty_sequence_because_a_derived_value_needs_an_input()
    {
        Assert.Throws<ArgumentException>(() =>
            MeasurementProvenanceRules.Combine(Array.Empty<MeasurementProvenance>()));
    }

    [Theory]
    [InlineData(MeasurementProvenance.Estimated, true)]
    [InlineData(MeasurementProvenance.Inferred, true)]
    [InlineData(MeasurementProvenance.Measured, false)]
    [InlineData(MeasurementProvenance.UpstreamReported, false)]
    [InlineData(MeasurementProvenance.ClientReported, false)]
    [InlineData(MeasurementProvenance.RuntimeLog, false)]
    public void Only_estimated_and_inferred_values_require_an_explicit_label(
        MeasurementProvenance provenance,
        bool expected)
    {
        Assert.Equal(expected, MeasurementProvenanceRules.RequiresExplicitLabel(provenance));
    }

    [Fact]
    public void TokenCount_rejects_a_negative_value()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TokenCount.Create(-1, MeasurementProvenance.UpstreamReported));
    }

    [Fact]
    public void Unreported_usage_is_unknown_rather_than_zero()
    {
        var usage = UsageObservation.Create();

        Assert.True(usage.IsUnknown);
        Assert.Null(usage.PromptTokens);
        Assert.Null(usage.CompletionTokens);
        Assert.Null(usage.TotalTokens);
        Assert.Null(usage.WeakestProvenance());
        Assert.Same(UsageObservation.Unknown, usage);
    }

    [Fact]
    public void Usage_keeps_each_component_independently_sourced()
    {
        var usage = UsageObservation.Create(
            promptTokens: TokenCount.FromGatewayEstimate(900),
            completionTokens: TokenCount.FromUpstream(64));

        Assert.False(usage.IsUnknown);
        Assert.Equal(MeasurementProvenance.Estimated, usage.PromptTokens?.Provenance);
        Assert.Equal(MeasurementProvenance.UpstreamReported, usage.CompletionTokens?.Provenance);
        Assert.Equal(MeasurementProvenance.Estimated, usage.WeakestProvenance());
    }

    [Fact]
    public void A_reported_total_is_not_recomputed_from_its_components()
    {
        // A runtime may include tokens AgentSplice cannot see, so the reported total is preserved
        // even when it does not equal prompt plus completion.
        var usage = UsageObservation.Create(
            promptTokens: TokenCount.FromUpstream(10),
            completionTokens: TokenCount.FromUpstream(5),
            totalTokens: TokenCount.FromUpstream(20));

        Assert.Equal(20, usage.TotalTokens?.Value);
    }
}
