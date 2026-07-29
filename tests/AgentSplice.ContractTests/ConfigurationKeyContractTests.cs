using System.Collections;
using System.Reflection;
using AgentSplice.Application.Configuration;
using AgentSplice.TestSupport;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace AgentSplice.ContractTests;

/// <summary>
/// Verifies that every AgentSplice environment variable in the deployment files addresses a real
/// setting.
/// </summary>
/// <remarks>
/// An environment variable that binds to nothing is silently ignored: the deployment looks configured
/// and runs on built-in defaults instead. That failure mode is invisible until someone wonders why a
/// Compose override had no effect, so the mapping from
/// <c>AGENTSPLICE__SECTION__PROPERTY</c> to the options tree is asserted here.
/// </remarks>
public sealed class ConfigurationKeyContractTests
{
    private const string Prefix = "AGENTSPLICE__";

    [Fact]
    public void Every_agentsplice_variable_in_the_compose_file_binds_to_a_real_setting()
    {
        var keys = ComposeEnvironmentKeys();

        Assert.NotEmpty(keys);
        AssertAllBind(keys, "docker-compose.yml");
    }

    [Fact]
    public void Every_agentsplice_variable_in_the_example_env_file_binds_to_a_real_setting()
    {
        var keys = EnvFileKeys();

        Assert.NotEmpty(keys);
        AssertAllBind(keys, ".env.example");
    }

    [Fact]
    public void The_compose_file_binds_the_gateway_to_loopback_on_the_host()
    {
        var compose = RepositoryPaths.ReadText("docker-compose.yml");

        Assert.Contains("\"127.0.0.1:5280:5280\"", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void The_compose_file_keeps_content_capture_off_by_default()
    {
        var compose = RepositoryPaths.ReadText("docker-compose.yml");

        Assert.Contains(
            "AGENTSPLICE__CAPTURE__CONTENTENABLED: ${AGENTSPLICE__CAPTURE__CONTENTENABLED:-false}",
            compose,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_example_env_file_ships_no_api_key_value()
    {
        foreach (var line in RepositoryPaths.ReadText(".env.example").Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("LM_STUDIO_API_KEY=", StringComparison.Ordinal))
            {
                Assert.Equal("LM_STUDIO_API_KEY=", trimmed);
            }
        }
    }

    [Fact]
    public void An_unbindable_key_is_detected_by_the_resolver()
    {
        // Guards the resolver itself: the pre-Stage-0 Compose file used AGENTSPLICE_PERSISTENCE_MODE
        // with single underscores, which binds to nothing. A resolver that accepted anything would
        // make the tests above vacuous.
        Assert.False(TryResolve("AGENTSPLICE__PERSISTENCE__MODEL", out _));
        Assert.False(TryResolve("AGENTSPLICE__NOSUCHSECTION__VALUE", out _));
        Assert.False(TryResolve("AGENTSPLICE__RUNTIMES__0__NOSUCHPROPERTY", out _));
        Assert.True(TryResolve("AGENTSPLICE__PERSISTENCE__MODE", out _));
        Assert.True(TryResolve("AGENTSPLICE__RUNTIMES__0__BASEURL", out _));
        Assert.True(TryResolve("AGENTSPLICE__RUNTIMES__0__DISCOVERY__CACHEDURATION", out _));
    }

    private static void AssertAllBind(IReadOnlyList<string> keys, string source)
    {
        var unbound = keys.Where(key => !TryResolve(key, out _)).ToArray();

        Assert.True(
            unbound.Length == 0,
            FormattableString.Invariant(
                $"{source} sets variables that bind to no setting: {string.Join(", ", unbound)}"));
    }

    private static List<string> ComposeEnvironmentKeys()
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(RepositoryPaths.ReadText("docker-compose.yml")));

        var services = (YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode)["services"];
        var keys = new List<string>();

        foreach (var service in services.Children.Values.OfType<YamlMappingNode>())
        {
            if (!service.Children.TryGetValue(new YamlScalarNode("environment"), out var environment)
                || environment is not YamlMappingNode mapping)
            {
                continue;
            }

            foreach (var name in mapping.Children.Keys.OfType<YamlScalarNode>())
            {
                if (name.Value?.StartsWith(Prefix, StringComparison.Ordinal) == true)
                {
                    keys.Add(name.Value);
                }
            }
        }

        return keys;
    }

    private static List<string> EnvFileKeys()
    {
        var keys = new List<string>();

        foreach (var line in RepositoryPaths.ReadText(".env.example").Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=', StringComparison.Ordinal);

            if (separator <= 0)
            {
                continue;
            }

            var name = trimmed[..separator];

            if (name.StartsWith(Prefix, StringComparison.Ordinal))
            {
                keys.Add(name);
            }
        }

        return keys;
    }

    /// <summary>
    /// Walks the options tree the way <c>Microsoft.Extensions.Configuration</c> would, so that a key
    /// resolves only if a bindable member actually exists at every segment.
    /// </summary>
    private static bool TryResolve(string environmentVariableName, out Type? leafType)
    {
        leafType = null;

        var segments = environmentVariableName
            .Replace("__", ":", StringComparison.Ordinal)
            .Split(':', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 2
            || !string.Equals(segments[0], "AGENTSPLICE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var current = typeof(AgentSpliceOptions);

        foreach (var segment in segments.Skip(1))
        {
            if (ElementTypeOf(current) is { } elementType)
            {
                // A list segment must be an index; the following segment addresses the element type.
                if (!int.TryParse(segment, out _))
                {
                    return false;
                }

                current = elementType;
                continue;
            }

            var property = current
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase));

            if (property is null)
            {
                return false;
            }

            current = property.PropertyType;
        }

        leafType = current;
        return true;
    }

    private static Type? ElementTypeOf(Type type)
    {
        if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type))
        {
            return null;
        }

        return type.IsGenericType ? type.GetGenericArguments()[0] : typeof(object);
    }
}
