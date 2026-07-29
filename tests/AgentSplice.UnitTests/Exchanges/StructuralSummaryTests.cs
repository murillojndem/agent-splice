using System.Globalization;
using AgentSplice.Domain.Exchanges;
using Xunit;

namespace AgentSplice.UnitTests.Exchanges;

/// <summary>
/// Structural summaries record shapes, not content (docs/SPECIFICATION.md FR-TRACE-003,
/// FR-TRACE-008). The bounds here stop a generated or adversarial request from turning a summary
/// into an unbounded store of caller-chosen strings.
/// </summary>
public sealed class StructuralSummaryTests
{
    [Fact]
    public void A_request_summary_records_counts_and_flags()
    {
        var summary = StructuralRequestSummary.Create(
            messageCount: 3,
            messageCountsByRole: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["system"] = 1,
                ["user"] = 1,
                ["assistant"] = 1,
            },
            toolDeclarationCount: 4,
            toolChoicePresent: true,
            streamRequested: true,
            streamOptionsPresent: true,
            requestBodyBytes: 8192);

        Assert.Equal(3, summary.MessageCount);
        Assert.Equal(1, summary.MessageCountsByRole["user"]);
        Assert.Equal(4, summary.ToolDeclarationCount);
        Assert.True(summary.ToolChoicePresent);
        Assert.True(summary.StreamRequested);
        Assert.True(summary.StreamOptionsPresent);
        Assert.Equal(8192L, summary.RequestBodyBytes);
    }

    [Fact]
    public void A_transparent_exchange_drops_no_fields()
    {
        var summary = StructuralRequestSummary.Create(
            messageCount: 1,
            unknownTopLevelFieldNames: ["reasoning_effort", "seed"]);

        Assert.Equal(["reasoning_effort", "seed"], summary.UnknownTopLevelFieldNames);
        Assert.Empty(summary.DroppedFieldNames);
    }

    [Fact]
    public void Unknown_field_names_are_deduplicated()
    {
        var summary = StructuralRequestSummary.Create(
            messageCount: 1,
            unknownTopLevelFieldNames: ["seed", "seed", "seed"]);

        Assert.Equal(["seed"], summary.UnknownTopLevelFieldNames);
    }

    [Fact]
    public void Unknown_field_names_are_bounded_in_count()
    {
        var names = Enumerable
            .Range(0, StructuralRequestSummary.MaxUnknownFieldNames + 10)
            .Select(index => "field" + index.ToString(CultureInfo.InvariantCulture));

        var summary = StructuralRequestSummary.Create(messageCount: 1, unknownTopLevelFieldNames: names);

        Assert.Equal(StructuralRequestSummary.MaxUnknownFieldNames, summary.UnknownTopLevelFieldNames.Count);
    }

    [Fact]
    public void Unknown_field_names_are_truncated_in_length()
    {
        var longName = new string('f', StructuralRequestSummary.MaxFieldNameLength + 20);

        var summary = StructuralRequestSummary.Create(
            messageCount: 1,
            unknownTopLevelFieldNames: [longName]);

        Assert.Equal(
            StructuralRequestSummary.MaxFieldNameLength,
            summary.UnknownTopLevelFieldNames[0].Length);
    }

    [Fact]
    public void Field_names_containing_control_characters_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => StructuralRequestSummary.Create(
            messageCount: 1,
            unknownTopLevelFieldNames: ["field\nname"]));
    }

    [Fact]
    public void Negative_counts_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StructuralRequestSummary.Create(messageCount: -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StructuralRequestSummary.Create(messageCount: 1, toolDeclarationCount: -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StructuralRequestSummary.Create(messageCount: 1, requestBodyBytes: -1));
    }

    [Fact]
    public void A_response_summary_records_stream_shape_and_native_tool_calls()
    {
        var summary = StructuralResponseSummary.Create(
            choiceCount: 1,
            finishReasons: ["stop"],
            nativeToolCallCount: 2,
            responseBodyBytes: 4096,
            streamEventCount: 37,
            usageReported: true);

        Assert.Equal(1, summary.ChoiceCount);
        Assert.Equal(["stop"], summary.FinishReasons);
        Assert.Equal(2, summary.NativeToolCallCount);
        Assert.Equal(4096L, summary.ResponseBodyBytes);
        Assert.Equal(37, summary.StreamEventCount);
        Assert.True(summary.UsageReported);
    }

    [Fact]
    public void A_non_streamed_response_summary_reports_no_events_and_no_usage_by_default()
    {
        var summary = StructuralResponseSummary.Create(choiceCount: 1);

        Assert.Equal(0, summary.StreamEventCount);
        Assert.False(summary.UsageReported);
        Assert.Empty(summary.FinishReasons);
    }

    [Fact]
    public void Finish_reasons_are_deduplicated_and_bounded()
    {
        var reasons = Enumerable
            .Range(0, StructuralResponseSummary.MaxFinishReasons + 5)
            .Select(index => "reason" + index.ToString(CultureInfo.InvariantCulture))
            .ToList();

        reasons.Insert(0, "reason0");

        var summary = StructuralResponseSummary.Create(finishReasons: reasons);

        Assert.Equal(StructuralResponseSummary.MaxFinishReasons, summary.FinishReasons.Count);
        Assert.Equal(summary.FinishReasons.Distinct(StringComparer.Ordinal).Count(), summary.FinishReasons.Count);
    }

    [Fact]
    public void Blank_finish_reasons_are_ignored()
    {
        var summary = StructuralResponseSummary.Create(finishReasons: ["", "   ", "stop"]);

        Assert.Equal(["stop"], summary.FinishReasons);
    }
}
