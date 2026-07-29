using AgentSplice.Application.Models;

namespace AgentSplice.Application.Protocols;

/// <summary>
/// Serialises a model catalogue in the ingress protocol's own shape.
/// </summary>
/// <remarks>
/// A port because <c>AgentSplice.Application</c> must not depend on a protocol module, and because
/// the compatibility shape is the protocol's concern: the OpenAI schema's requirement that
/// <c>created</c> be a present integer is satisfied inside the writer, so no compatibility sentinel
/// ever leaks back into the catalogue that reasoning and evidence are built from.
/// </remarks>
public interface IModelListWriter
{
    /// <summary>The media type the written payload carries.</summary>
    string MediaType { get; }

    /// <summary>Writes the catalogue as a UTF-8 payload.</summary>
    byte[] Write(IReadOnlyList<ModelCatalogueEntry> entries);
}
