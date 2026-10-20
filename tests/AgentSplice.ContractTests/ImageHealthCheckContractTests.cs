using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentSplice.HealthProbe;
using AgentSplice.TestSupport;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace AgentSplice.ContractTests;

/// <summary>
/// The image's <c>HEALTHCHECK</c> runs the probe the image carries, against the port the image
/// listens on, with a target the probe accepts (ADR 0016, FR-HEALTH-001).
/// </summary>
/// <remarks>
/// Every failure here produces an image that builds, starts, and serves every request — and that
/// Docker reports as unhealthy forever, or reports nothing about at all. A health check naming a
/// tool the image lacks, a port nothing listens on, or a target the probe refuses fails only once
/// the container is running, and only where someone is looking at its health. These tests read the
/// files a container is built from, so the drift is caught where it is made.
/// </remarks>
public sealed partial class ImageHealthCheckContractTests
{
    private const string ProbeAssembly = "AgentSplice.HealthProbe.dll";

    [Fact]
    public void The_image_health_check_runs_the_probe_in_exec_form_against_liveness()
    {
        var healthCheck = ReadHealthCheck();

        // Exec form: a shell form needs /bin/sh, and so does every curl-and-pipe idiom.
        Assert.Equal(3, healthCheck.Command.Length);
        Assert.Equal("dotnet", healthCheck.Command[0]);
        Assert.EndsWith("/" + ProbeAssembly, healthCheck.Command[1], StringComparison.Ordinal);

        // Liveness, never readiness: readiness can answer 503 because LM Studio is down, and the
        // container would then report the runtime's outage as the gateway's (FR-HEALTH-002).
        Assert.Equal("/health/live", new Uri(healthCheck.Command[2]).AbsolutePath);
    }

    [Fact]
    public void The_health_check_target_is_one_the_probe_accepts()
    {
        var target = ReadHealthCheck().Command[2];

        Assert.True(
            LivenessProbe.TryParseTarget(target, out _),
            FormattableString.Invariant($"The probe refuses '{target}': it takes an http URL on a loopback IP address."));
    }

    [Fact]
    public void The_health_check_port_is_the_port_the_image_listens_on()
    {
        var probed = new Uri(ReadHealthCheck().Command[2]).Port;

        var runtime = RuntimeStage();

        var imageUrls = ImageUrls().Match(runtime);
        Assert.True(imageUrls.Success, "The runtime stage must set ASPNETCORE_URLS.");
        Assert.Equal(probed, SinglePort(imageUrls.Groups["urls"].Value));

        var exposed = Exposed().Match(runtime);
        Assert.True(exposed.Success, "The runtime stage must EXPOSE the port it listens on.");
        Assert.Equal(probed, int.Parse(exposed.Groups["port"].Value, CultureInfo.InvariantCulture));

        // Compose sets ASPNETCORE_URLS again and publishes a port; both must still be the probed one,
        // because the image's health check is what the Compose service runs.
        var service = ComposeService("agentsplice");

        var environment = Assert.IsType<YamlMappingNode>(service.Children[new YamlScalarNode("environment")]);
        var composeUrls = Assert.IsType<YamlScalarNode>(environment.Children[new YamlScalarNode("ASPNETCORE_URLS")]);
        Assert.Equal(probed, SinglePort(composeUrls.Value!));

        var ports = Assert.IsType<YamlSequenceNode>(service.Children[new YamlScalarNode("ports")]);
        var mapping = Assert.IsType<YamlScalarNode>(Assert.Single(ports.Children)).Value!;
        var containerPort = mapping.Split(':')[^1].Split('/')[0];
        Assert.Equal(probed, int.Parse(containerPort, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void The_image_binds_only_the_urls_it_states()
    {
        // The base image sets ASPNETCORE_HTTP_PORTS=8080. Beside ASPNETCORE_URLS it binds nothing, but
        // ASP.NET Core then warns at every start that it is overriding it: the first line a new user read
        // in `docker logs` (the cold test of 0.2.2-alpha), about a port nothing uses.
        var runtime = RuntimeStage();

        Assert.True(
            HttpPortsCleared().IsMatch(runtime),
            "The runtime stage must clear ASPNETCORE_HTTP_PORTS, which the base image sets beside the URLS it binds.");
    }

    [Fact]
    public void The_health_check_runs_the_probe_the_image_copies()
    {
        var stages = Stages();

        var build = Assert.Single(stages, stage => stage.Contains(" AS build", StringComparison.Ordinal));
        var published = ProbePublish().Match(build);
        Assert.True(published.Success, "The build stage must publish the health probe.");

        var output = published.Groups["output"].Value;

        var copy = BuildCopies().Matches(stages[^1])
            .SingleOrDefault(candidate => candidate.Groups["source"].Value == output);

        Assert.True(copy is not null, FormattableString.Invariant($"The runtime stage must copy {output} out of the build stage."));

        // The runtime stage's WORKDIR is /app (PackagingContractTests), so ./probe/ is /app/probe/.
        Assert.Equal(
            "/app/" + copy.Groups["target"].Value + "/" + ProbeAssembly,
            ReadHealthCheck().Command[1]);
    }

    [Fact]
    public void The_probe_gives_up_before_docker_does()
    {
        // Docker kills a check that outlives --timeout and records nothing it said. The probe's own
        // budget must expire first, so a stalled gateway is reported with a reason.
        var options = ReadHealthCheck().Options;

        Assert.True(options.TryGetValue("timeout", out var timeout), "The health check must set --timeout.");
        Assert.True(
            LivenessProbe.RequestTimeout < DockerDuration(timeout),
            FormattableString.Invariant($"The probe waits {LivenessProbe.RequestTimeout}; Docker kills it after {timeout}."));
    }

    [Fact]
    public void The_compose_service_inherits_the_image_health_check()
    {
        // An override here would be what runs under Compose, and the checks above would be proving
        // a health check nobody uses.
        Assert.False(
            ComposeService("agentsplice").Children.ContainsKey(new YamlScalarNode("healthcheck")),
            "docker-compose.yml must not override the image's health check.");
    }

    private static HealthCheck ReadHealthCheck()
    {
        var all = HealthChecks().Matches(Joined(RepositoryPaths.ReadText("Dockerfile")));

        // Docker honours only the last HEALTHCHECK, so more than one means a dead one that reads as
        // if it applied.
        var match = Assert.Single(all);
        Assert.Contains(match.Value, RuntimeStage(), StringComparison.Ordinal);

        var options = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Capture option in match.Groups["option"].Captures)
        {
            var separator = option.Value.IndexOf('=', StringComparison.Ordinal);
            options[option.Value[2..separator]] = option.Value[(separator + 1)..];
        }

        var command = JsonSerializer.Deserialize<string[]>(match.Groups["command"].Value)
            ?? throw new InvalidOperationException("The HEALTHCHECK command is not a JSON array.");

        return new HealthCheck(options, command);
    }

    private static string[] Stages() =>
        Joined(RepositoryPaths.ReadText("Dockerfile")).Split("\nFROM ", StringSplitOptions.None);

    private static string RuntimeStage() => Stages()[^1];

    /// <summary>The Dockerfile with continuation lines joined, so one instruction is one line.</summary>
    private static string Joined(string dockerfile) =>
        LineContinuation().Replace(dockerfile.Replace("\r\n", "\n", StringComparison.Ordinal), " ");

    private static int SinglePort(string urls)
    {
        var url = Assert.Single(urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return new Uri(url).Port;
    }

    /// <summary>A Docker duration such as <c>5s</c>, <c>1m30s</c>, or <c>500ms</c>.</summary>
    private static TimeSpan DockerDuration(string value)
    {
        var parts = DurationParts().Matches(value);

        Assert.True(
            parts.Count > 0 && string.Concat(parts.Select(part => part.Value)) == value,
            FormattableString.Invariant($"'{value}' is not a Docker duration this test understands."));

        return parts.Aggregate(TimeSpan.Zero, (total, part) =>
        {
            var amount = double.Parse(part.Groups["amount"].Value, CultureInfo.InvariantCulture);

            return total + part.Groups["unit"].Value switch
            {
                "ms" => TimeSpan.FromMilliseconds(amount),
                "s" => TimeSpan.FromSeconds(amount),
                "m" => TimeSpan.FromMinutes(amount),
                _ => TimeSpan.FromHours(amount),
            };
        });
    }

    private static YamlMappingNode ComposeService(string name)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(RepositoryPaths.ReadText("docker-compose.yml")));

        var services = (YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode)["services"];

        return Assert.IsType<YamlMappingNode>(services.Children[new YamlScalarNode(name)]);
    }

    private sealed record HealthCheck(IReadOnlyDictionary<string, string> Options, string[] Command);

    [GeneratedRegex(@"\\[ \t]*\n")]
    private static partial Regex LineContinuation();

    [GeneratedRegex(@"^HEALTHCHECK\s+(?:(?<option>--[a-z-]+=\S+)\s+)*CMD\s+(?<command>\[.*\])\s*$", RegexOptions.Multiline)]
    private static partial Regex HealthChecks();

    [GeneratedRegex(@"ASPNETCORE_URLS=(?<urls>\S+)")]
    private static partial Regex ImageUrls();

    [GeneratedRegex(@"\bASPNETCORE_HTTP_PORTS=(?:""""|(?=\s|$))")]
    private static partial Regex HttpPortsCleared();

    [GeneratedRegex(@"^EXPOSE\s+(?<port>\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex Exposed();

    [GeneratedRegex(@"^RUN dotnet publish src/AgentSplice\.HealthProbe/AgentSplice\.HealthProbe\.csproj\s.*?--output (?<output>\S+)", RegexOptions.Multiline)]
    private static partial Regex ProbePublish();

    [GeneratedRegex(@"^COPY --from=build (?<source>\S+) \./(?<target>[^/\s]+)/?\s*$", RegexOptions.Multiline)]
    private static partial Regex BuildCopies();

    [GeneratedRegex(@"(?<amount>\d+(?:\.\d+)?)(?<unit>ms|s|m|h)")]
    private static partial Regex DurationParts();
}
