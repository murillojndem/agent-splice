using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentSplice.Api.Correlation;
using AgentSplice.Api.Hosting;
using AgentSplice.TestSupport;
using Xunit;

namespace AgentSplice.IntegrationTests.Hosting;

/// <summary>
/// The sample client configuration and the client guide name the address, the headers, and the
/// credentials the gateway actually has (ADR 0019).
/// </summary>
/// <remarks>
/// A file-reading test here rather than in the contract suite because the address is the gateway's own
/// loopback default, which the contract project cannot see: it does not reference
/// <c>AgentSplice.Api</c>. A sample or guide that drifted from it — a host name, a port, a package that
/// speaks another protocol — would still parse and still look right, and would fail only in a user's
/// client, as a refused connection or a <c>404</c> that names no cause.
/// </remarks>
public sealed partial class ClientSampleContractTests
{
    private const string ProviderId = "agentsplice";

    [Fact]
    public void The_opencode_sample_targets_the_gateways_loopback_default()
    {
        using var sample = OpenCodeSample();
        var provider = sample.RootElement.GetProperty("provider").GetProperty(ProviderId);

        Assert.Equal(
            LoopbackBindingDefault.Urls + "/v1",
            provider.GetProperty("options").GetProperty("baseURL").GetString());

        // The OpenAI package calls /v1/responses, which the gateway does not serve; the compatible
        // package speaks Chat Completions.
        Assert.Equal("@ai-sdk/openai-compatible", provider.GetProperty("npm").GetString());
    }

    [Fact]
    public void Every_model_the_opencode_sample_uses_goes_through_the_gateway()
    {
        // OpenCode sends title and summary requests to its small model, and picks one from another
        // provider when none is named; an agent, mode, or command can name a model of its own. A
        // sample that pointed any of them elsewhere would send part of every session around the
        // gateway, where no evidence of it is kept.
        using var sample = OpenCodeSample();
        var root = sample.RootElement;

        Assert.Equal(
            new[] { ProviderId },
            root.GetProperty("provider").EnumerateObject().Select(provider => provider.Name).ToArray());

        var models = root
            .GetProperty("provider")
            .GetProperty(ProviderId)
            .GetProperty("models")
            .EnumerateObject()
            .Select(model => model.Name)
            .ToHashSet(StringComparer.Ordinal);

        var references = new List<(string Path, string Value)>();
        CollectModelReferences(root, "$", references);

        Assert.Contains(references, reference => reference.Path == "$.model");
        Assert.Contains(references, reference => reference.Path == "$.small_model");

        foreach (var (path, value) in references)
        {
            Assert.True(
                value.StartsWith(ProviderId + "/", StringComparison.Ordinal)
                    && models.Contains(value[(ProviderId.Length + 1)..]),
                FormattableString.Invariant(
                    $"The OpenCode sample's {path} is '{value}', which is not a model of the '{ProviderId}' provider."));
        }
    }

    [Fact]
    public void The_opencode_sample_carries_no_credential()
    {
        // The gateway reads no client credential, so a key in the sample would buy nothing, and a
        // project's opencode.json is a file people commit.
        using var sample = OpenCodeSample();
        var findings = new List<string>();

        CollectCredentialShapes(sample.RootElement, "$", findings);

        Assert.True(
            findings.Count == 0,
            FormattableString.Invariant(
                $"The OpenCode sample carries credential-shaped values: {string.Join(", ", findings)}"));
    }

    [Fact]
    public void The_client_guides_use_the_ipv4_loopback_literal()
    {
        // The gateway listens on 127.0.0.1 only. A client given a host name may resolve it to the
        // IPv6 loopback first and be refused, so no guide may teach either form.
        var hostName = FormattableString.Invariant($"localhost:{GatewayAddress().Port}");
        var files = GuideFiles();

        Assert.Contains(files, file => file.Name.StartsWith("examples/", StringComparison.Ordinal));

        foreach (var (name, text) in files)
        {
            Assert.False(
                text.Contains(hostName, StringComparison.OrdinalIgnoreCase),
                FormattableString.Invariant($"{name} points a client at {hostName}."));
            Assert.False(
                text.Contains("[::1]", StringComparison.Ordinal),
                FormattableString.Invariant($"{name} points a client at the IPv6 loopback."));
        }
    }

    [Fact]
    public void The_client_guides_name_the_gateways_own_address()
    {
        // The base URL is the value a user copies. A wrong port in it would pass every other check
        // here and fail as a refused connection — or, if it were the runtime's own port, not fail at
        // all, and send every request around the gateway. The guides also name the runtime's address
        // when they talk about LM Studio, so its port is accepted on those lines and nowhere else.
        var gateway = GatewayAddress();
        var runtime = ShippedRuntimeAddress();

        foreach (var (name, text) in new[] { ("README.md", RepositoryPaths.ReadText("README.md")), ("docs/CLIENTS.md", RepositoryPaths.ReadText("docs", "CLIENTS.md")) })
        {
            Assert.Contains(LoopbackBindingDefault.Urls + "/v1", text, StringComparison.Ordinal);

            foreach (var line in text.Split('\n'))
            {
                var aboutTheRuntime = line.Contains("LM Studio", StringComparison.Ordinal);

                foreach (Match match in LoopbackAddressPattern().Matches(line))
                {
                    var port = int.Parse(match.Groups["port"].Value, CultureInfo.InvariantCulture);

                    Assert.True(
                        string.Equals(match.Groups["host"].Value, gateway.Host, StringComparison.Ordinal)
                            && (port == gateway.Port || (aboutTheRuntime && port == runtime.Port)),
                        FormattableString.Invariant(
                            $"{name} names {match.Value} where a client would read it, and that is not the gateway's address."));
                }
            }
        }
    }

    [Fact]
    public void The_client_guide_names_the_exchange_header_the_gateway_returns()
    {
        var guide = RepositoryPaths.ReadText("docs", "CLIENTS.md");

        Assert.Contains(GatewayHeaderNames.ExchangeId, guide, StringComparison.Ordinal);

        // Header names are case-insensitive, so a misspelling in any case is still a misspelling.
        var declared = typeof(GatewayHeaderNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, text) in new[] { ("docs/CLIENTS.md", guide), ("README.md", RepositoryPaths.ReadText("README.md")) })
        {
            foreach (Match match in GatewayHeaderPattern().Matches(text))
            {
                Assert.True(
                    declared.Contains(match.Value),
                    FormattableString.Invariant($"{name} names '{match.Value}', which the gateway does not send."));
            }
        }
    }

    private static JsonDocument OpenCodeSample() =>
        JsonDocument.Parse(RepositoryPaths.ReadText("examples", "opencode", "opencode.json"));

    private static Uri GatewayAddress() => new(LoopbackBindingDefault.Urls);

    private static Uri ShippedRuntimeAddress()
    {
        using var settings = JsonDocument.Parse(RepositoryPaths.ReadText("src", "AgentSplice.Api", "appsettings.json"));

        return new Uri(settings.RootElement
            .GetProperty("agentsplice")
            .GetProperty("runtimes")[0]
            .GetProperty("baseUrl")
            .GetString()!);
    }

    private static List<(string Name, string Text)> GuideFiles()
    {
        var files = new List<(string Name, string Text)>
        {
            ("README.md", RepositoryPaths.ReadText("README.md")),
            ("docs/INSTALLATION.md", RepositoryPaths.ReadText("docs", "INSTALLATION.md")),
            ("docs/CLIENTS.md", RepositoryPaths.ReadText("docs", "CLIENTS.md")),
            ("docs/SPECIFICATION.md", RepositoryPaths.ReadText("docs", "SPECIFICATION.md")),
        };

        var examples = RepositoryPaths.Resolve("examples");

        foreach (var path in Directory.GetFiles(examples, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(RepositoryPaths.Root.FullName, path).Replace('\\', '/');
            files.Add((relative, File.ReadAllText(path)));
        }

        return files;
    }

    private static void CollectModelReferences(JsonElement element, string path, List<(string Path, string Value)> references)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var child = FormattableString.Invariant($"{path}.{property.Name}");

                    // The provider's own model table is keyed by ID and holds descriptions, not
                    // references, so it is not walked.
                    if (child == FormattableString.Invariant($"$.provider.{ProviderId}.models"))
                    {
                        continue;
                    }

                    if (property.Value.ValueKind == JsonValueKind.String
                        && (property.Name == "model" || property.Name == "small_model"))
                    {
                        references.Add((child, property.Value.GetString() ?? string.Empty));
                    }

                    CollectModelReferences(property.Value, child, references);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;

                foreach (var item in element.EnumerateArray())
                {
                    CollectModelReferences(item, FormattableString.Invariant($"{path}[{index++}]"), references);
                }

                break;
        }
    }

    private static void CollectCredentialShapes(JsonElement element, string path, List<string> findings)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var child = FormattableString.Invariant($"{path}.{property.Name}");

                    // A provider's env list names variables OpenCode reads the key from, which is
                    // the same as writing the key in.
                    if (property.Name.Equals("apiKey", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("headers", StringComparison.OrdinalIgnoreCase)
                        || (property.Name.Equals("env", StringComparison.OrdinalIgnoreCase)
                            && path.StartsWith("$.provider.", StringComparison.Ordinal)))
                    {
                        findings.Add(child);
                    }

                    CollectCredentialShapes(property.Value, child, findings);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;

                foreach (var item in element.EnumerateArray())
                {
                    CollectCredentialShapes(item, FormattableString.Invariant($"{path}[{index++}]"), findings);
                }

                break;

            case JsonValueKind.String:
                // OpenCode substitutes {env:NAME} and {file:path}; either would pull a secret into a
                // request the gateway discards.
                var value = element.GetString() ?? string.Empty;

                if (value.Contains("{env:", StringComparison.Ordinal)
                    || value.Contains("{file:", StringComparison.Ordinal))
                {
                    findings.Add(path);
                }

                break;
        }
    }

    [GeneratedRegex("x-agentsplice-[a-z]+(?:-[a-z]+)*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GatewayHeaderPattern();

    [GeneratedRegex(@"https?://(?<host>127\.0\.0\.1|localhost|\[::1\]):(?<port>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LoopbackAddressPattern();
}
