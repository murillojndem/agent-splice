using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Application.Protocols;

/// <summary>
/// What a protocol module could establish about a completion request, without reading its content.
/// </summary>
/// <remarks>
/// Carries no message text, no tool arguments, and no field values — only shapes, counts, names, and
/// the byte span of the one value routing may have to replace.
///
/// <see cref="ModelValueStart"/> and <see cref="ModelValueEnd"/> are what make byte-preserving
/// forwarding possible. Re-serialising a parsed document would be semantically equivalent but not
/// byte-identical, because a JSON writer normalises escape forms and number formatting; an
/// exact-forwarding test built on that would only prove that our own parser round-trips, which is
/// the opposite of what it exists to show.
/// </remarks>
public sealed record ChatCompletionEnvelope
{
    private ChatCompletionEnvelope()
    {
    }

    /// <summary>The model identifier the client sent.</summary>
    public ClientModelId Model { get; private init; }

    /// <summary>True when the client asked for a streamed response.</summary>
    public bool StreamRequested { get; private init; }

    /// <summary>The privacy-safe description of what arrived.</summary>
    public StructuralRequestSummary Summary { get; private init; } = null!;

    /// <summary>Index of the opening quote of the top-level <c>model</c> value.</summary>
    public int ModelValueStart { get; private init; }

    /// <summary>Index one past the closing quote of the top-level <c>model</c> value.</summary>
    public int ModelValueEnd { get; private init; }

    /// <summary>Creates a parsed envelope.</summary>
    public static ChatCompletionEnvelope Create(
        ClientModelId model,
        bool streamRequested,
        StructuralRequestSummary summary,
        int modelValueStart,
        int modelValueEnd)
    {
        if (model.IsEmpty)
        {
            throw new ArgumentException("An envelope requires the requested model.", nameof(model));
        }

        ArgumentNullException.ThrowIfNull(summary);
        ArgumentOutOfRangeException.ThrowIfNegative(modelValueStart);

        if (modelValueEnd <= modelValueStart)
        {
            throw new ArgumentOutOfRangeException(
                nameof(modelValueEnd),
                modelValueEnd,
                "The model value span must be non-empty.");
        }

        return new ChatCompletionEnvelope
        {
            Model = model,
            StreamRequested = streamRequested,
            Summary = summary,
            ModelValueStart = modelValueStart,
            ModelValueEnd = modelValueEnd,
        };
    }
}
