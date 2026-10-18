using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentSplice.Application.Configuration;
using AgentSplice.ContractTests.Documents;
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
public sealed partial class ConfigurationKeyContractTests
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
    public void Every_agentsplice_variable_in_the_user_guides_binds_to_a_real_setting()
    {
        // The guides tell people which setting to change when a timeout fires or a runtime moves. A
        // name that binds to nothing is accepted without complaint and changes nothing, so the advice
        // would read as followed while the gateway kept its defaults. A name that stops at a section
        // is the same failure: a value assigned to it reaches no setting.
        var keys = new List<string>();

        foreach (var (name, text) in UserGuides.Read())
        {
            var guideKeys = GuideKeys(text);
            var unbound = guideKeys
                .Where(key => !TryResolve(key, out var leafType) || !IsSettingValue(leafType!))
                .ToArray();

            keys.AddRange(guideKeys);

            Assert.True(
                unbound.Length == 0,
                FormattableString.Invariant(
                    $"{name} names settings that bind to no value: {string.Join(", ", unbound)}"));
        }

        Assert.NotEmpty(keys);
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
    public void The_shipped_settings_name_a_persistence_mode_this_build_implements()
    {
        // Asserted against the file rather than through a booted host, because the integration
        // factory forces persistence off so that tests do not each create a database. That override
        // is right, and it means no test exercises the shipped persistence block any more — leaving
        // a wrong mode or an empty connection string to surface as a failed startup in production
        // and nowhere else.
        var persistence = ShippedSettings()
            .RootElement
            .GetProperty("agentsplice")
            .GetProperty("persistence");

        var mode = persistence.GetProperty("mode").GetString();

        Assert.True(
            Enum.TryParse<PersistenceMode>(mode, out var parsed),
            FormattableString.Invariant($"appsettings.json names an unknown persistence mode '{mode}'."));

        Assert.True(
            parsed is PersistenceMode.Sqlite or PersistenceMode.None,
            FormattableString.Invariant(
                $"appsettings.json selects '{parsed}', which has no provider in this build. Shipping it would fail every startup."));

        if (parsed != PersistenceMode.None)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(persistence.GetProperty("connectionString").GetString()),
                "A persistence mode other than None requires a connection string; startup validation refuses without one.");
        }
    }

    [Fact]
    public void The_shipped_settings_keep_the_store_from_logging_the_statements_it_runs()
    {
        // EF Core logs every command at Information by default. Parameters are redacted unless
        // sensitive-data logging is switched on, so this is noise rather than leakage today — but the
        // distance between the two is one setting, and a store that narrates itself into the default
        // log is the wrong side of "no request or response bodies in default logs" to be sitting on.
        var levels = ShippedSettings()
            .RootElement
            .GetProperty("Logging")
            .GetProperty("LogLevel");

        Assert.Equal(
            "Warning",
            levels.GetProperty("Microsoft.EntityFrameworkCore.Database.Command").GetString());
    }

    [Fact]
    public void The_compose_configuration_satisfies_the_administrative_binding_guard()
    {
        // The Compose service sets ASPNETCORE_URLS to 0.0.0.0, which AdministrationBindingGuard
        // correctly treats as network-reachable and refuses without an administrative credential.
        // The shipped configuration therefore has to supply one, and this asserts the two halves
        // agree — otherwise the repository's own default container does not start, which is exactly
        // what happened when the guard landed (FR-HEALTH-006).
        var compose = RepositoryPaths.ReadText("docker-compose.yml");

        Assert.Contains(
            "AGENTSPLICE__ADMINISTRATION__APIKEYENVIRONMENTVARIABLE: AGENTSPLICE_ADMIN_API_KEY",
            compose,
            StringComparison.Ordinal);

        // Required, with no default. A shipped default token is a token everybody knows.
        Assert.Contains("${AGENTSPLICE_ADMIN_API_KEY:?", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void The_example_env_file_documents_the_administrative_token_without_assigning_one()
    {
        // Every guide generates the token and appends its own assignment. An empty one shipped here
        // came first in every .env built that way, and the client guide's extraction read it: 400 in
        // bash, 401 in PowerShell. A shipped value would be a token everybody knows.
        var env = RepositoryPaths.ReadText(".env.example");

        Assert.Contains("AGENTSPLICE_ADMIN_API_KEY=", env, StringComparison.Ordinal);

        Assert.DoesNotContain(
            env.Split('\n').Select(line => line.Trim()),
            line => line.StartsWith("AGENTSPLICE_ADMIN_API_KEY", StringComparison.Ordinal));
    }

    [Fact]
    public void The_example_env_file_does_not_advertise_settings_this_build_refuses()
    {
        // Both were accurate before the validator started refusing them, and a sample file that
        // offers a setting the gateway rejects at startup is a first run that fails for a reason the
        // documentation caused.
        var env = RepositoryPaths.ReadText(".env.example");

        Assert.DoesNotContain("None, Sqlite, or Postgres", env, StringComparison.Ordinal);
        Assert.Contains("Postgres is refused at startup", env, StringComparison.Ordinal);
        Assert.Contains("Refused at startup if set to true", env, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_settings_keep_telemetry_export_off()
    {
        // Exported telemetry describes every exchange; sending it off the process is a decision the
        // operator makes, never one a fresh install has already made (ADR 0014).
        var otlp = ShippedSettings()
            .RootElement
            .GetProperty("agentsplice")
            .GetProperty("telemetry")
            .GetProperty("otlp");

        Assert.False(otlp.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void The_compose_file_keeps_telemetry_export_off_by_default()
    {
        var compose = RepositoryPaths.ReadText("docker-compose.yml");

        Assert.Contains(
            "AGENTSPLICE__TELEMETRY__OTLP__ENABLED: ${AGENTSPLICE__TELEMETRY__OTLP__ENABLED:-false}",
            compose,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_deployment_files_do_not_offer_an_endpoint_variable_the_gateway_ignores()
    {
        // The example file once set OTEL_EXPORTER_OTLP_ENDPOINT, which nothing read. It reads as a
        // working setting and would now be doubly wrong: the gateway deliberately ignores it, so an
        // operator following the example would believe export was pointed somewhere it was not.
        foreach (var line in RepositoryPaths.ReadText(".env.example").Split('\n'))
        {
            Assert.False(
                line.Trim().StartsWith("OTEL_EXPORTER_OTLP_ENDPOINT=", StringComparison.Ordinal),
                ".env.example sets OTEL_EXPORTER_OTLP_ENDPOINT, which AgentSplice does not read.");
        }

        Assert.DoesNotContain(ComposeEnvironmentKeys(includeAll: true), key => key == "OTEL_EXPORTER_OTLP_ENDPOINT");
    }

    [Fact]
    public void The_compose_collector_is_opt_in_and_publishes_nothing_on_the_host()
    {
        var collector = ComposeService("otel-collector");

        var profiles = Assert.IsType<YamlSequenceNode>(collector.Children[new YamlScalarNode("profiles")]);
        Assert.Contains(profiles.Children.OfType<YamlScalarNode>(), profile => profile.Value == "otel");

        // Reachable on the Compose network only. A published receiver would accept telemetry from
        // anything on the host's network and put it in the same log as the gateway's.
        Assert.False(
            collector.Children.ContainsKey(new YamlScalarNode("ports")),
            "The collector publishes a port on the host.");
    }

    [Fact]
    public void The_compose_default_endpoint_is_one_the_shipped_collector_listens_on()
    {
        // The two files are edited separately, and a port that drifted in either would leave export
        // switched on and every batch refused — which, by design, nothing on the request path reports.
        var compose = RepositoryPaths.ReadText("docker-compose.yml");
        Assert.Contains(
            "AGENTSPLICE__TELEMETRY__OTLP__ENDPOINT: ${AGENTSPLICE__TELEMETRY__OTLP__ENDPOINT:-http://otel-collector:4318}",
            compose,
            StringComparison.Ordinal);
        Assert.Contains(
            "AGENTSPLICE__TELEMETRY__OTLP__PROTOCOL: ${AGENTSPLICE__TELEMETRY__OTLP__PROTOCOL:-HttpProtobuf}",
            compose,
            StringComparison.Ordinal);

        var stream = new YamlStream();
        stream.Load(new StringReader(RepositoryPaths.ReadText("config", "otel-collector.yaml")));

        var protocols = (YamlMappingNode)((YamlMappingNode)((YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode)
            ["receivers"])["otlp"])["protocols"];

        Assert.Equal("0.0.0.0:4318", ((YamlScalarNode)((YamlMappingNode)protocols["http"])["endpoint"]).Value);
        Assert.Equal("0.0.0.0:4317", ((YamlScalarNode)((YamlMappingNode)protocols["grpc"])["endpoint"]).Value);
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

    [Fact]
    public void Both_spellings_a_guide_uses_are_read_as_settings()
    {
        // Guards the extraction behind the user-guide test. A pattern that matched nothing, stopped at
        // a single underscore, or skipped an argument spelled with capitals would cut a misspelled
        // name back to a section that exists, and let it through.
        var keys = GuideKeys(
            """
            $env:AGENTSPLICE__RUNTIMES__0__TIMEOUT__IDLESTREAM = '00:03:00'
            .\AgentSplice.Api.exe --agentsplice:runtimes:0:timeouts:responseHeaders=00:03:00
            $env:AGENTSPLICE__PERSISTENCE_MODE = 'None'
            .\AgentSplice.Api.exe --AgentSplice:Runtimes:0:Timeouts_IdleStream=00:03:00
            Any AGENTSPLICE__* variable, and AGENTSPLICE_ADMIN_API_KEY, which names a secret.
            """);

        Assert.Collection(
            keys,
            key => AssertUnbound("AGENTSPLICE__RUNTIMES__0__TIMEOUT__IDLESTREAM", key),
            key =>
            {
                Assert.Equal("agentsplice:runtimes:0:timeouts:responseHeaders", key);
                Assert.True(TryResolve(key, out var leafType));
                Assert.True(IsSettingValue(leafType!));
            },
            key => AssertUnbound("AGENTSPLICE__PERSISTENCE_MODE", key),
            key => AssertUnbound("AgentSplice:Runtimes:0:Timeouts_IdleStream", key));

        // A name cut short at a section resolves, and is still not a setting a value can be given to.
        Assert.True(TryResolve("AGENTSPLICE__RUNTIMES__0__TIMEOUTS", out var section));
        Assert.False(IsSettingValue(section!));

        static void AssertUnbound(string expected, string key)
        {
            Assert.Equal(expected, key);
            Assert.False(TryResolve(key, out _));
        }
    }

    private static JsonDocument ShippedSettings() =>
        JsonDocument.Parse(RepositoryPaths.ReadText("src", "AgentSplice.Api", "appsettings.json"));

    private static void AssertAllBind(IReadOnlyList<string> keys, string source)
    {
        var unbound = keys.Where(key => !TryResolve(key, out _)).ToArray();

        Assert.True(
            unbound.Length == 0,
            FormattableString.Invariant(
                $"{source} sets variables that bind to no setting: {string.Join(", ", unbound)}"));
    }

    private static YamlMappingNode ComposeServices()
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(RepositoryPaths.ReadText("docker-compose.yml")));

        return (YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode)["services"];
    }

    private static YamlMappingNode ComposeService(string name) =>
        Assert.IsType<YamlMappingNode>(ComposeServices().Children[new YamlScalarNode(name)]);

    private static List<string> ComposeEnvironmentKeys(bool includeAll = false)
    {
        var services = ComposeServices();
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
                if (name.Value is { } value
                    && (includeAll || value.StartsWith(Prefix, StringComparison.Ordinal)))
                {
                    keys.Add(value);
                }
            }
        }

        return keys;
    }

    /// <summary>
    /// Every setting a guide names, as an environment variable or as a command-line argument.
    /// </summary>
    /// <remarks>
    /// A name is read whole, through single underscores and in any case, so a misspelling reaches the
    /// resolver rather than being cut back to a section that exists. The environment form needs a
    /// segment after the prefix, so the glob <c>AGENTSPLICE__*</c> and a secret's name such as
    /// <c>AGENTSPLICE_ADMIN_API_KEY</c> are not mistaken for settings. The argument form loses its
    /// leading dashes and stops at the equals sign; the resolver reads colons and double underscores
    /// alike.
    /// </remarks>
    private static List<string> GuideKeys(string text)
    {
        var keys = new List<string>();

        foreach (Match match in GuideSettingPattern().Matches(text))
        {
            keys.Add(match.Groups["environment"].Success
                ? match.Groups["environment"].Value
                : match.Groups["argument"].Value);
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

    /// <summary>
    /// True when a resolved name ends at a value a setting can hold, rather than at a section.
    /// </summary>
    private static bool IsSettingValue(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        return underlying.IsPrimitive
            || underlying.IsEnum
            || underlying == typeof(string)
            || underlying == typeof(decimal)
            || underlying == typeof(TimeSpan)
            || underlying == typeof(Uri);
    }

    [GeneratedRegex(
        "(?<environment>AGENTSPLICE__[A-Za-z0-9_]+)|--(?<argument>agentsplice(?::[A-Za-z0-9_]+)+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GuideSettingPattern();
}
