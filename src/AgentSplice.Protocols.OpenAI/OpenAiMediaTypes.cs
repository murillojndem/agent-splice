namespace AgentSplice.Protocols.OpenAI;

/// <summary>Media types the OpenAI-compatible surface uses.</summary>
public static class OpenAiMediaTypes
{
    /// <summary>Request and response bodies on the non-streaming path.</summary>
    public const string Json = "application/json";

    /// <summary>Streamed responses. Declared for completeness; Stage 1A never emits one.</summary>
    public const string EventStream = "text/event-stream";
}
