using AgentSplice.TestSupport;
using YamlDotNet.RepresentationModel;

namespace AgentSplice.ContractTests.Documents;

/// <summary>
/// Minimal reader over <c>openapi/agentsplice-openapi.yaml</c> for contract assertions.
/// </summary>
/// <remarks>
/// Reads the YAML representation model directly instead of an OpenAPI object model. The document is
/// OpenAPI 3.1 and the assertions only need path keys and enum members, so a schema-aware reader
/// would add a dependency without adding confidence.
/// </remarks>
internal sealed class OpenApiDocument
{
    private readonly YamlMappingNode root;

    private OpenApiDocument(YamlMappingNode root) => this.root = root;

    internal static OpenApiDocument Load()
    {
        var text = RepositoryPaths.ReadText("openapi", "agentsplice-openapi.yaml");

        var stream = new YamlStream();
        stream.Load(new StringReader(text));

        return new OpenApiDocument((YamlMappingNode)stream.Documents[0].RootNode);
    }

    /// <summary>Every declared path template, verbatim.</summary>
    internal IReadOnlyList<string> Paths() =>
        Mapping(root, "paths")
            .Children
            .Keys
            .Select(key => ((YamlScalarNode)key).Value!)
            .ToArray();

    /// <summary>The enum members of a property on a component schema.</summary>
    internal IReadOnlyList<string> SchemaPropertyEnum(string schemaName, string propertyName)
    {
        var schemas = Mapping(Mapping(root, "components"), "schemas");
        var properties = Mapping(Mapping(schemas, schemaName), "properties");
        var property = Mapping(properties, propertyName);

        return ((YamlSequenceNode)property["enum"])
            .Children
            .Select(child => ((YamlScalarNode)child).Value!)
            .ToArray();
    }

    private static YamlMappingNode Mapping(YamlMappingNode parent, string key)
    {
        if (!parent.Children.TryGetValue(new YamlScalarNode(key), out var child))
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"The OpenAPI document has no '{key}' node where one was expected."));
        }

        return (YamlMappingNode)child;
    }
}
