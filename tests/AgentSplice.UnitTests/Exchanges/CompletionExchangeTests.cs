using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Measurements;
using Xunit;

namespace AgentSplice.UnitTests.Exchanges;

/// <summary>
/// Exchange lifecycle rules (docs/SPECIFICATION.md section 13.3, FR-TRACE-002, FR-TRACE-010).
/// </summary>
public sealed class CompletionExchangeTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_exchange_claims_no_content_retention()
    {
        var exchange = Accept();

        Assert.Equal(ContentRetentionState.Disabled, exchange.ContentRetentionState);
    }

    [Fact]
    public void A_new_exchange_identifies_its_protocol_mode_and_requested_model()
    {
        var exchange = Accept(streaming: true);

        Assert.Equal(IngressProtocol.OpenAiChatCompletions, exchange.IngressProtocol);
        Assert.True(exchange.Streaming);
        Assert.Equal("local-coder", exchange.ClientModelId.Value);
        Assert.Equal(ExchangeStatus.Accepted, exchange.Status);
        Assert.Null(exchange.CompletedAt);
        Assert.Null(exchange.FailureClass);
    }

    [Fact]
    public void A_new_exchange_has_no_runtime_or_upstream_model_before_resolution()
    {
        var exchange = Accept();

        Assert.Null(exchange.Resolution);
        Assert.Null(exchange.RuntimeEndpointId);
        Assert.Null(exchange.UpstreamModelId);
    }

    [Fact]
    public void A_streamed_exchange_starts_with_an_unknown_termination()
    {
        Assert.Equal(StreamTermination.Unknown, Accept(streaming: true).StreamTermination);
    }

    [Fact]
    public void A_non_streamed_exchange_has_no_stream_termination()
    {
        Assert.Equal(StreamTermination.NotApplicable, Accept().StreamTermination);
    }

    [Fact]
    public void Resolve_records_the_runtime_and_upstream_model()
    {
        var exchange = Accept().Resolve(AliasResolution());

        Assert.Equal(ExchangeStatus.Forwarding, exchange.Status);
        Assert.Equal("lmstudio-local", exchange.RuntimeEndpointId?.Value);
        Assert.Equal("qwen3.6-27b-mtp", exchange.UpstreamModelId?.Value);
    }

    [Fact]
    public void Resolve_rejects_a_resolution_for_a_different_requested_model()
    {
        var mismatched = ModelResolution.PassThrough(
            ClientModelId.Create("some-other-model"),
            RuntimeEndpointId.Create("lmstudio-local"),
            UpstreamModelId.Create("some-other-model"));

        Assert.Throws<ArgumentException>(() => Accept().Resolve(mismatched));
    }

    [Fact]
    public void An_alias_that_changes_the_model_identifier_is_a_routing_change()
    {
        Assert.True(AliasResolution().IsRoutingChange);
    }

    [Fact]
    public void A_passthrough_resolution_is_not_a_routing_change()
    {
        var resolution = ModelResolution.PassThrough(
            ClientModelId.Create("qwen3.6-27b-mtp"),
            RuntimeEndpointId.Create("lmstudio-local"),
            UpstreamModelId.Create("qwen3.6-27b-mtp"));

        Assert.False(resolution.IsRoutingChange);
    }

    [Fact]
    public void An_alias_sourced_resolution_must_record_its_alias()
    {
        Assert.Equal("local-coder", AliasResolution().Alias?.Value);
    }

    [Fact]
    public void Complete_finalises_a_non_streamed_exchange()
    {
        var exchange = Accept().Resolve(AliasResolution()).Complete(Origin.AddSeconds(3));

        Assert.Equal(ExchangeStatus.Completed, exchange.Status);
        Assert.Equal(Origin.AddSeconds(3), exchange.CompletedAt);
        Assert.True(exchange.IsTerminal);
        Assert.Null(exchange.FailureClass);
    }

    [Fact]
    public void Complete_requires_a_streamed_exchange_to_state_how_its_stream_ended()
    {
        var exchange = Accept(streaming: true).Resolve(AliasResolution());

        Assert.Throws<ArgumentException>(() => exchange.Complete(Origin.AddSeconds(3)));
    }

    [Fact]
    public void Complete_refuses_a_stream_termination_on_a_non_streamed_exchange()
    {
        var exchange = Accept().Resolve(AliasResolution());

        Assert.Throws<ArgumentException>(
            () => exchange.Complete(Origin.AddSeconds(3), StreamTermination.NormalCompletion));
    }

    [Fact]
    public void Complete_records_the_protocol_terminator_for_a_streamed_exchange()
    {
        var exchange = Accept(streaming: true)
            .Resolve(AliasResolution())
            .BeginStreaming()
            .Complete(Origin.AddSeconds(3), StreamTermination.ProtocolTerminatorReceived);

        Assert.Equal(StreamTermination.ProtocolTerminatorReceived, exchange.StreamTermination);
    }

    [Fact]
    public void A_terminal_exchange_cannot_be_completed_again()
    {
        var exchange = Accept().Resolve(AliasResolution()).Complete(Origin.AddSeconds(1));

        Assert.Throws<InvalidOperationException>(() => exchange.Complete(Origin.AddSeconds(2)));
    }

    [Fact]
    public void A_terminal_exchange_cannot_be_resolved_again()
    {
        var exchange = Accept().Resolve(AliasResolution()).Complete(Origin.AddSeconds(1));

        Assert.Throws<InvalidOperationException>(() => exchange.Resolve(AliasResolution()));
    }

    [Fact]
    public void Cancel_records_the_cancellation_failure_class()
    {
        var exchange = Accept(streaming: true)
            .Resolve(AliasResolution())
            .BeginStreaming()
            .Cancel(Origin.AddSeconds(2));

        Assert.Equal(ExchangeStatus.Cancelled, exchange.Status);
        Assert.Equal(FailureClass.RequestCancelled, exchange.FailureClass);
        Assert.Equal(StreamTermination.ClientCancelled, exchange.StreamTermination);
    }

    [Fact]
    public void Fail_records_the_stable_failure_class_and_termination()
    {
        var exchange = Accept(streaming: true)
            .Resolve(AliasResolution())
            .Fail(FailureClass.UpstreamTimeout, Origin.AddSeconds(30), StreamTermination.Timeout);

        Assert.Equal(ExchangeStatus.Failed, exchange.Status);
        Assert.Equal(FailureClass.UpstreamTimeout, exchange.FailureClass);
        Assert.Equal(StreamTermination.Timeout, exchange.StreamTermination);
    }

    [Fact]
    public void Fail_before_routing_leaves_the_runtime_unknown()
    {
        var exchange = Accept().Fail(FailureClass.ModelNotFound, Origin.AddMilliseconds(2));

        Assert.Null(exchange.RuntimeEndpointId);
        Assert.Equal(FailureClass.ModelNotFound, exchange.FailureClass);
    }

    [Fact]
    public void BeginStreaming_is_invalid_for_a_non_streamed_exchange()
    {
        var exchange = Accept().Resolve(AliasResolution());

        Assert.Throws<InvalidOperationException>(exchange.BeginStreaming);
    }

    [Fact]
    public void Usage_is_unknown_until_it_is_attached()
    {
        Assert.True(Accept().Usage.IsUnknown);
    }

    [Fact]
    public void Attaching_usage_preserves_component_provenance()
    {
        var exchange = Accept().WithUsage(UsageObservation.Create(
            promptTokens: TokenCount.FromUpstream(120),
            completionTokens: TokenCount.FromUpstream(48)));

        Assert.Equal(MeasurementProvenance.UpstreamReported, exchange.Usage.PromptTokens?.Provenance);
    }

    [Fact]
    public void Summaries_are_absent_until_they_are_created()
    {
        var exchange = Accept();

        Assert.Null(exchange.RequestSummary);
        Assert.Null(exchange.ResponseSummary);
    }

    [Fact]
    public void Accept_rejects_an_exchange_without_an_identity()
    {
        Assert.Throws<ArgumentException>(() => CompletionExchange.Accept(
            default,
            PublicRequestId.Create("req-1"),
            IngressProtocol.OpenAiChatCompletions,
            ClientModelId.Create("local-coder"),
            streaming: false,
            Origin));
    }

    [Fact]
    public void Accept_rejects_an_exchange_without_a_public_request_identifier()
    {
        Assert.Throws<ArgumentException>(() => CompletionExchange.Accept(
            ExchangeId.New(),
            default,
            IngressProtocol.OpenAiChatCompletions,
            ClientModelId.Create("local-coder"),
            streaming: false,
            Origin));
    }

    [Fact]
    public void Accept_rejects_an_exchange_without_a_requested_model()
    {
        Assert.Throws<ArgumentException>(() => CompletionExchange.Accept(
            ExchangeId.New(),
            PublicRequestId.Create("req-1"),
            IngressProtocol.OpenAiChatCompletions,
            default,
            streaming: false,
            Origin));
    }

    private static CompletionExchange Accept(bool streaming = false) =>
        CompletionExchange.Accept(
            ExchangeId.New(),
            PublicRequestId.Create("req-1"),
            IngressProtocol.OpenAiChatCompletions,
            ClientModelId.Create("local-coder"),
            streaming,
            Origin);

    private static ModelResolution AliasResolution() =>
        ModelResolution.FromAlias(
            ClientModelId.Create("local-coder"),
            ModelAliasId.Create("local-coder"),
            RuntimeEndpointId.Create("lmstudio-local"),
            UpstreamModelId.Create("qwen3.6-27b-mtp"));
}
