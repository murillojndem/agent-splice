using AgentSplice.Domain.Identifiers;
using Xunit;

namespace AgentSplice.UnitTests.Identifiers;

/// <summary>
/// Identifier validation. These bounds are what keep correlation tokens out of response headers and
/// out of metric dimensions (docs/SPECIFICATION.md FR-OBS-006, docs/API.md header rules).
/// </summary>
public sealed class IdentifierTests
{
    [Fact]
    public void ExchangeId_New_produces_distinct_non_empty_identities()
    {
        var first = ExchangeId.New();
        var second = ExchangeId.New();

        Assert.NotEqual(first, second);
        Assert.False(first.IsEmpty);
        Assert.False(second.IsEmpty);
    }

    [Fact]
    public void ExchangeId_From_rejects_the_empty_guid()
    {
        var exception = Assert.Throws<ArgumentException>(() => ExchangeId.From(Guid.Empty));
        Assert.Equal("value", exception.ParamName);
    }

    [Fact]
    public void ExchangeId_default_is_reported_as_empty()
    {
        Assert.True(default(ExchangeId).IsEmpty);
    }

    [Fact]
    public void ExchangeId_round_trips_through_its_canonical_string()
    {
        var original = ExchangeId.New();

        Assert.Equal(original, ExchangeId.Parse(original.ToString()));
    }

    [Theory]
    [InlineData("req-123")]
    [InlineData("A.B_C:D")]
    [InlineData("0123456789")]
    public void PublicRequestId_accepts_printable_ascii(string value)
    {
        Assert.Equal(value, PublicRequestId.Create(value).Value);
    }

    [Theory]
    [InlineData("req\n123")]
    [InlineData("req\t123")]
    [InlineData("café")]
    public void PublicRequestId_rejects_values_that_could_be_smuggled_into_a_header(string value)
    {
        Assert.False(PublicRequestId.TryCreate(value, out var requestId));
        Assert.True(requestId.IsEmpty);
    }

    [Fact]
    public void PublicRequestId_rejects_values_longer_than_the_documented_maximum()
    {
        var tooLong = new string('a', PublicRequestId.MaxLength + 1);

        Assert.False(PublicRequestId.TryCreate(tooLong, out _));
        Assert.True(PublicRequestId.TryCreate(new string('a', PublicRequestId.MaxLength), out _));
    }

    [Fact]
    public void PublicRequestId_can_be_derived_from_an_exchange_identity()
    {
        var exchangeId = ExchangeId.New();

        Assert.Equal(exchangeId.ToString(), PublicRequestId.FromExchangeId(exchangeId).Value);
    }

    [Fact]
    public void TraceId_accepts_a_w3c_trace_identifier()
    {
        const string Value = "4bf92f3577b34da6a3ce929d0e0e4736";

        Assert.Equal(Value, TraceId.Create(Value).Value);
    }

    [Theory]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("4BF92F3577B34DA6A3CE929D0E0E4736")]
    [InlineData("4bf92f3577b34da6a3ce929d0e0e47")]
    [InlineData("zzf92f3577b34da6a3ce929d0e0e4736")]
    public void TraceId_rejects_values_that_are_not_valid_trace_identifiers(string value)
    {
        Assert.False(TraceId.TryCreate(value, out _));
    }

    [Fact]
    public void RuntimeEndpointId_normalises_case_so_metric_dimensions_stay_bounded()
    {
        Assert.Equal("lmstudio-local", RuntimeEndpointId.Create("LMStudio-Local").Value);
    }

    [Theory]
    [InlineData("lm studio")]
    [InlineData("lmstudio/local")]
    [InlineData("lmstudio:local")]
    public void RuntimeEndpointId_rejects_characters_outside_the_slug_charset(string value)
    {
        Assert.False(RuntimeEndpointId.TryCreate(value, out _));
    }

    [Theory]
    [InlineData("qwen3.6-27b-mtp")]
    [InlineData("org/model:tag")]
    [InlineData("model@2024-01-01")]
    public void Model_identifiers_accept_the_punctuation_real_model_names_use(string value)
    {
        Assert.Equal(value, UpstreamModelId.Create(value).Value);
        Assert.Equal(value, ClientModelId.Create(value).Value);
        Assert.Equal(value, ModelAliasId.Create(value).Value);
    }

    [Theory]
    [InlineData("model name")]
    [InlineData("Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf")]
    [InlineData("publisher/repo#branch")]
    [InlineData("model (draft)")]
    [InlineData("modèle-français")]
    [InlineData("模型-中文")]
    [InlineData("model+variant=2")]
    public void Model_identifiers_accept_opaque_third_party_values(string value)
    {
        // A model identifier is chosen by a runtime, a registry, or a model author. Rejecting a value
        // the runtime would have accepted would make AgentSplice the source of a failure that does
        // not exist downstream, which is the opposite of transparent forwarding (P-002).
        Assert.Equal(value, UpstreamModelId.Create(value).Value);
        Assert.Equal(value, ClientModelId.Create(value).Value);
        Assert.Equal(value, ModelAliasId.Create(value).Value);
    }

    [Fact]
    public void Model_identifiers_preserve_case_because_clients_echo_them_verbatim()
    {
        Assert.Equal("Qwen3.6-27B", ClientModelId.Create("Qwen3.6-27B").Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Model_identifiers_reject_blank_values(string value)
    {
        Assert.False(ClientModelId.TryCreate(value, out _));
    }

    [Fact]
    public void Model_identifiers_reject_control_characters()
    {
        // Built in code rather than declared as inline data: NUL and DEL have no readable escape in
        // an attribute argument, and embedding them literally would put invisible control bytes in
        // the source file.
        string[] values =
        [
            "model\nname",
            "model\tname",
            "model" + (char)0x00 + "name",
            "model" + (char)0x1b + "name",
            "model" + (char)0x7f + "name",
        ];

        foreach (var value in values)
        {
            // Control characters are the one class AgentSplice cannot carry: they permit log and
            // header injection wherever the identifier is later rendered.
            Assert.False(ClientModelId.TryCreate(value, out _));
            Assert.False(UpstreamModelId.TryCreate(value, out _));
            Assert.False(ModelAliasId.TryCreate(value, out _));
        }
    }

    [Fact]
    public void Model_identifiers_reject_text_that_cannot_be_encoded_as_utf8()
    {
        // A lone high surrogate has no UTF-8 encoding, so such an identifier could never be
        // forwarded upstream, persisted, or exported.
        Assert.False(ClientModelId.TryCreate("model-\ud83d", out _));
    }

    [Fact]
    public void Model_identifiers_accept_a_well_formed_surrogate_pair()
    {
        const string Value = "model-🚀";

        Assert.Equal(Value, ClientModelId.Create(Value).Value);
    }

    [Fact]
    public void Model_identifiers_reject_values_longer_than_the_documented_maximum()
    {
        Assert.False(ClientModelId.TryCreate(new string('a', ClientModelId.MaxLength + 1), out _));
        Assert.True(ClientModelId.TryCreate(new string('a', ClientModelId.MaxLength), out _));
    }

    [Fact]
    public void Model_identifiers_reject_null()
    {
        Assert.False(ClientModelId.TryCreate(null, out _));
    }
}
