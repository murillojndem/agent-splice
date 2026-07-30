using System.Text;
using AgentSplice.Application.Configuration;
using AgentSplice.Protocols.OpenAI.ChatCompletions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentSplice.UnitTests.Protocols;

/// <summary>
/// The unsupported-field policy (docs/SPECIFICATION.md FR-CHAT-005, docs/API.md).
/// </summary>
/// <remarks>
/// FR-CHAT-005 asks for an <em>explicit</em> policy. Both behaviours are defensible; what the
/// requirement rules out is a deployment that cannot tell which one it has.
/// </remarks>
public sealed class CompatibilityModeTests
{
    private const string WithUnknownField =
        """{"model":"m","messages":[{"role":"user","content":"hi"}],"seed":7}""";

    [Fact]
    public void Transparent_is_the_default()
    {
        // The runtime is the authority on its own protocol, so refusing a field it would have
        // accepted makes AgentSplice the source of a failure that does not exist downstream.
        Assert.Equal(CompatibilityMode.Transparent, new CompatibilityOptions().UnsupportedFields);
    }

    [Fact]
    public void Transparent_forwards_a_field_the_gateway_does_not_model()
    {
        var result = Codec(CompatibilityMode.Transparent).Read(Encoding.UTF8.GetBytes(WithUnknownField));

        Assert.True(result.Succeeded);
        Assert.Equal(["seed"], result.Envelope!.Summary.UnknownTopLevelFieldNames);
    }

    [Fact]
    public void Strict_refuses_a_field_the_gateway_does_not_model()
    {
        var result = Codec(CompatibilityMode.Strict).Read(Encoding.UTF8.GetBytes(WithUnknownField));

        Assert.False(result.Succeeded);
        Assert.Equal(400, result.Error!.StatusCode);
    }

    [Fact]
    public void Strict_names_the_offending_field()
    {
        var result = Codec(CompatibilityMode.Strict).Read(Encoding.UTF8.GetBytes(WithUnknownField));

        Assert.Equal("seed", result.Error!.Param);
    }

    [Fact]
    public void Strict_accepts_a_request_built_only_from_modelled_fields()
    {
        const string Modelled =
            """{"model":"m","messages":[{"role":"user"}],"temperature":0.5,"max_tokens":10,"top_p":1}""";

        Assert.True(Codec(CompatibilityMode.Strict).Read(Encoding.UTF8.GetBytes(Modelled)).Succeeded);
    }

    [Fact]
    public void Strict_constrains_only_top_level_fields()
    {
        // A nested property belongs to a shape AgentSplice does not claim to understand, so refusing
        // it would be enforcing a schema the gateway never had.
        const string NestedExtras =
            """{"model":"m","messages":[{"role":"user","content":"hi","name":"bob","extra":{"a":1}}]}""";

        Assert.True(Codec(CompatibilityMode.Strict).Read(Encoding.UTF8.GetBytes(NestedExtras)).Succeeded);
    }

    [Fact]
    public void Strict_still_reports_a_malformed_body_as_malformed()
    {
        // The policy governs unmodelled fields, not parsing. A body that is not JSON must not be
        // reported as a policy violation.
        var result = Codec(CompatibilityMode.Strict).Read(Encoding.UTF8.GetBytes("not json"));

        Assert.False(result.Succeeded);
        Assert.Null(result.Error!.Param);
    }

    private static OpenAiChatCompletionRequestCodec Codec(CompatibilityMode mode) =>
        new(Options.Create(new AgentSpliceOptions
        {
            Compatibility = new CompatibilityOptions { UnsupportedFields = mode },
        }));
}
