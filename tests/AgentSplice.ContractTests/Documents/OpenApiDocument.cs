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

    /// <summary>The operations under a path, keyed by verb.</summary>
    internal IReadOnlyDictionary<string, YamlMappingNode> Operations(string path)
    {
        var operations = Mapping(Mapping(root, "paths"), path);

        return operations.Children.ToDictionary(
            entry => ((YamlScalarNode)entry.Key).Value!,
            entry => (YamlMappingNode)entry.Value,
            StringComparer.Ordinal);
    }

    /// <summary>The response status codes an operation declares.</summary>
    internal static IReadOnlyList<string> ResponseStatuses(YamlMappingNode operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return Mapping(operation, "responses")
            .Children
            .Keys
            .Select(key => ((YamlScalarNode)key).Value!)
            .ToArray();
    }

    /// <summary>The security scheme names an operation requires, or an empty list when it requires none.</summary>
    internal static IReadOnlyList<string> SecuritySchemes(YamlMappingNode operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (!operation.Children.TryGetValue(new YamlScalarNode("security"), out var security))
        {
            return [];
        }

        return ((YamlSequenceNode)security)
            .Children
            .OfType<YamlMappingNode>()
            .SelectMany(requirement => requirement.Children.Keys)
            .Select(key => ((YamlScalarNode)key).Value!)
            .ToArray();
    }

    /// <summary>Names of the declared security schemes.</summary>
    internal IReadOnlyList<string> SecuritySchemeNames() =>
        Mapping(Mapping(root, "components"), "securitySchemes")
            .Children
            .Keys
            .Select(key => ((YamlScalarNode)key).Value!)
            .ToArray();

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
