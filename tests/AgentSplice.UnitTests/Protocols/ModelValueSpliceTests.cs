using System.Text;
using AgentSplice.Application.Protocols;
using AgentSplice.Domain.Identifiers;
using AgentSplice.Protocols.OpenAI.ChatCompletions;
using Xunit;

namespace AgentSplice.UnitTests.Protocols;

/// <summary>
/// Byte preservation when routing renames the model (ADR 0008 decision 1).
/// </summary>
/// <remarks>
/// These are the tests the whole forwarding design exists to satisfy. Each asserts on the raw bytes,
/// not on a reparsed document: re-emitting a parsed request would pass a semantic comparison while
/// silently normalising escape forms and number formatting, and "nothing else changed" would then be
/// a statement about our parser rather than about what the runtime received.
/// </remarks>
public sealed class ModelValueSpliceTests
{
    private readonly OpenAiChatCompletionRequestCodec codec = new();

    [Fact]
    public void Substitution_changes_only_the_model_value()
    {
        const string Body = """{"model":"alias","messages":[{"role":"user","content":"hi"}]}""";

        Assert.Equal(
            """{"model":"real-target","messages":[{"role":"user","content":"hi"}]}""",
            Splice(Body, "real-target"));
    }

    [Fact]
    public void An_escape_sequence_elsewhere_survives_byte_for_byte()
    {
        // Utf8JsonWriter would re-emit "A" as "A". Semantically identical, byte-different, and
        // therefore not transparent forwarding.
        const string Body = """{"model":"alias","messages":[{"role":"user","content":"A\n\t\\"}]}""";

        Assert.Equal(
            """{"model":"real","messages":[{"role":"user","content":"A\n\t\\"}]}""",
            Splice(Body, "real"));
    }

    [Fact]
    public void Number_formatting_survives_byte_for_byte()
    {
        // A writer would render 1.0 as 1 and 1e2 as 100.
        const string Body = """{"model":"alias","temperature":1.0,"top_p":1e2,"messages":[{"role":"user"}]}""";

        Assert.Equal(
            """{"model":"real","temperature":1.0,"top_p":1e2,"messages":[{"role":"user"}]}""",
            Splice(Body, "real"));
    }

    [Fact]
    public void Insignificant_whitespace_survives_byte_for_byte()
    {
        const string Body = "{\n  \"model\" : \"alias\" ,\n  \"messages\" : [ { \"role\": \"user\" } ]\n}";

        Assert.Equal(
            "{\n  \"model\" : \"real\" ,\n  \"messages\" : [ { \"role\": \"user\" } ]\n}",
            Splice(Body, "real"));
    }

    [Fact]
    public void Property_order_is_preserved()
    {
        const string Body = """{"messages":[{"role":"user"}],"temperature":0.5,"model":"alias","seed":7}""";

        Assert.Equal(
            """{"messages":[{"role":"user"}],"temperature":0.5,"model":"real","seed":7}""",
            Splice(Body, "real"));
    }

    [Fact]
    public void Substitution_works_when_model_is_the_last_property()
    {
        const string Body = """{"messages":[{"role":"user"}],"model":"alias"}""";

        Assert.Equal("""{"messages":[{"role":"user"}],"model":"real"}""", Splice(Body, "real"));
    }

    [Fact]
    public void A_model_property_nested_inside_a_message_is_not_touched()
    {
        // Only the top-level field routes the request. Rewriting a nested one would corrupt content.
        const string Body =
            """{"model":"alias","messages":[{"role":"user","content":"use model: alias","model":"alias"}]}""";

        Assert.Equal(
            """{"model":"real","messages":[{"role":"user","content":"use model: alias","model":"alias"}]}""",
            Splice(Body, "real"));
    }

    [Fact]
    public void An_escaped_quote_inside_the_model_value_does_not_end_the_span_early()
    {
        // The closing-quote search walks raw bytes, so a backslash-escaped quote must not terminate
        // the literal. Getting this wrong would splice into the middle of the document.
        const string Body = """{"model":"weird\"name","messages":[{"role":"user"}]}""";

        Assert.Equal(
            """{"model":"real","messages":[{"role":"user"}]}""",
            Splice(Body, "real"));
    }

    [Fact]
    public void A_model_value_written_with_an_escape_is_replaced_correctly()
    {
        const string Body = """{"model":"abc","messages":[{"role":"user"}]}""";

        Assert.Equal("""{"model":"real","messages":[{"role":"user"}]}""", Splice(Body, "real"));
    }

    [Fact]
    public void A_replacement_containing_json_significant_characters_is_encoded()
    {
        // Model identifiers are opaque third-party values and may contain a quote or a backslash.
        // Spliced in raw they would produce a malformed document.
        const string Body = """{"model":"alias","messages":[{"role":"user"}]}""";

        const string Awkward = """weird"name\with-escapes""";

        var spliced = Splice(Body, Awkward);

        // The escape form is the encoder's choice; what matters is that the raw characters were not
        // copied in unencoded, which would have produced a document the runtime could not parse.
        Assert.DoesNotContain("""weird"name""", spliced, StringComparison.Ordinal);
        Assert.Equal(Awkward, Reparse(spliced));
    }

    [Fact]
    public void A_multibyte_value_elsewhere_survives_byte_for_byte()
    {
        const string Body = """{"model":"alias","messages":[{"role":"user","content":"日本語 🚀"}]}""";

        Assert.Equal(
            """{"model":"real","messages":[{"role":"user","content":"日本語 🚀"}]}""",
            Splice(Body, "real"));
    }

    [Fact]
    public void A_multibyte_model_identifier_is_spliced_correctly()
    {
        const string Body = """{"model":"alias","messages":[{"role":"user"}]}""";

        Assert.Equal("模型-中文", Reparse(Splice(Body, "模型-中文")));
    }

    [Fact]
    public void A_spliced_body_reparses_to_the_same_document_apart_from_the_model()
    {
        const string Body =
            """{"model":"alias","messages":[{"role":"user","content":"hi"}],"seed":42,"stop":["a","b"]}""";

        var spliced = Splice(Body, "real");

        using var original = System.Text.Json.JsonDocument.Parse(Body);
        using var rewritten = System.Text.Json.JsonDocument.Parse(spliced);

        Assert.Equal(
            original.RootElement.GetProperty("messages").GetRawText(),
            rewritten.RootElement.GetProperty("messages").GetRawText());
        Assert.Equal(
            original.RootElement.GetProperty("stop").GetRawText(),
            rewritten.RootElement.GetProperty("stop").GetRawText());
        Assert.Equal(42, rewritten.RootElement.GetProperty("seed").GetInt32());
        Assert.Equal("real", rewritten.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void An_empty_replacement_is_rejected()
    {
        const string Body = """{"model":"alias","messages":[{"role":"user"}]}""";

        Assert.Throws<ArgumentException>(() => codec.SubstituteModel(
            Encoding.UTF8.GetBytes(Body),
            Envelope(Body),
            default));
    }

    private string Splice(string body, string replacement) =>
        Encoding.UTF8.GetString(codec.SubstituteModel(
            Encoding.UTF8.GetBytes(body),
            Envelope(body),
            UpstreamModelId.Create(replacement)));

    private ChatCompletionEnvelope Envelope(string body)
    {
        var read = codec.Read(Encoding.UTF8.GetBytes(body));

        Assert.True(read.Succeeded, "The fixture body must parse before it can be spliced.");

        return read.Envelope!;
    }

    private static string? Reparse(string spliced)
    {
        using var document = System.Text.Json.JsonDocument.Parse(spliced);
        return document.RootElement.GetProperty("model").GetString();
    }
}
