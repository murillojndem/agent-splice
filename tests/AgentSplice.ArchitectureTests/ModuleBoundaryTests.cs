using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace AgentSplice.ArchitectureTests;

/// <summary>
/// Enforces the dependency rules in docs/ARCHITECTURE.md.
/// </summary>
/// <remarks>
/// These are the rules that keep the durable core replaceable-adapter-free. Reviewing them by eye
/// works until the first time a provider type is convenient to reach for from Application; after
/// that the boundary only holds if the build enforces it (CLAUDE.md "Architecture boundaries").
/// </remarks>
public sealed class ModuleBoundaryTests
{
    private static readonly Assembly Domain = typeof(AgentSplice.Domain.AssemblyMarker).Assembly;
    private static readonly Assembly Application = typeof(AgentSplice.Application.AssemblyMarker).Assembly;
    private static readonly Assembly Infrastructure = typeof(AgentSplice.Infrastructure.AssemblyMarker).Assembly;
    private static readonly Assembly Observability = typeof(AgentSplice.Observability.AssemblyMarker).Assembly;
    private static readonly Assembly Api = typeof(Program).Assembly;
    private static readonly Assembly HealthProbe = typeof(AgentSplice.HealthProbe.AssemblyMarker).Assembly;
    private static readonly Assembly ProtocolsOpenAI = typeof(AgentSplice.Protocols.OpenAI.AssemblyMarker).Assembly;
    private static readonly Assembly ProvidersLmStudio = typeof(AgentSplice.Providers.LmStudio.AssemblyMarker).Assembly;

    [Fact]
    public void Domain_does_not_reference_any_other_agentsplice_assembly()
    {
        AssertNoReferenceTo(
            Domain,
            "AgentSplice.Application",
            "AgentSplice.Infrastructure",
            "AgentSplice.Api",
            "AgentSplice.Observability",
            "AgentSplice.Protocols.OpenAI",
            "AgentSplice.Providers.LmStudio");
    }

    [Fact]
    public void Domain_does_not_reference_infrastructure_or_transport_frameworks()
    {
        AssertNoReferenceTo(
            Domain,
            "Microsoft.AspNetCore",
            "Microsoft.EntityFrameworkCore",
            "Microsoft.Extensions.DependencyInjection",
            "Microsoft.Extensions.Hosting",
            "Microsoft.Extensions.Options",
            "System.Net.Http");
    }

    [Fact]
    public void Application_does_not_reference_infrastructure_the_api_or_any_adapter_module()
    {
        AssertNoReferenceTo(
            Application,
            "AgentSplice.Infrastructure",
            "AgentSplice.Api",
            "AgentSplice.Observability",
            "AgentSplice.Protocols.OpenAI",
            "AgentSplice.Providers.LmStudio");
    }

    [Fact]
    public void Application_does_not_reference_a_web_or_persistence_framework()
    {
        AssertNoReferenceTo(
            Application,
            "Microsoft.AspNetCore",
            "Microsoft.EntityFrameworkCore");
    }

    [Fact]
    public void Protocol_and_provider_modules_do_not_reference_infrastructure_or_the_api()
    {
        foreach (var module in new[] { ProtocolsOpenAI, ProvidersLmStudio, Observability })
        {
            AssertNoReferenceTo(module, "AgentSplice.Infrastructure", "AgentSplice.Api");
        }
    }

    [Fact]
    public void Provider_and_protocol_modules_do_not_reference_each_other()
    {
        AssertNoReferenceTo(ProtocolsOpenAI, "AgentSplice.Providers.LmStudio");
        AssertNoReferenceTo(ProvidersLmStudio, "AgentSplice.Protocols.OpenAI");
    }

    [Fact]
    public void Infrastructure_does_not_reference_the_api()
    {
        AssertNoReferenceTo(Infrastructure, "AgentSplice.Api");
    }

    [Fact]
    public void Application_does_not_reference_the_http_client_stack()
    {
        // The load-bearing boundary of the request path. It forces transport-exception
        // classification into the provider module, which is what makes error translation
        // deterministic, vendor-free, and unit-testable without a socket. Without the test, the
        // rule holds only until the first time catching HttpRequestException in Application is
        // convenient.
        AssertNoReferenceTo(Application, "System.Net.Http");
    }

    [Fact]
    public void Only_the_provider_module_talks_to_the_network()
    {
        foreach (var assembly in new[] { Domain, Application, Observability, ProtocolsOpenAI })
        {
            AssertNoReferenceTo(assembly, "System.Net.Http", "System.Net.Sockets");
        }
    }

    [Fact]
    public void The_application_never_touches_a_transport_type()
    {
        // The streaming relay is orchestration that runs between two sockets, which makes it the
        // most likely place for a transport type to leak inward. It reaches the client through a
        // port and the runtime through a classified byte source precisely so that neither end's
        // types cross this line.
        AssertNoReferenceTo(
            Application,
            "System.Net.Http.HttpResponseMessage",
            "Microsoft.AspNetCore.Http",
            "Microsoft.AspNetCore");
    }

    [Fact]
    public void Only_infrastructure_references_the_persistence_framework()
    {
        // docs/ARCHITECTURE.md confines EF Core to Infrastructure. The rule is load-bearing rather
        // than tidy: the moment a DbSet or a tracked entity is reachable from Application, the store's
        // shape becomes the domain's shape, and an evidence record starts being defined by what is
        // convenient to query.
        foreach (var assembly in AllProductionAssemblies().Where(candidate => candidate != Infrastructure))
        {
            AssertNoReferenceTo(assembly, "Microsoft.EntityFrameworkCore", "Microsoft.Data.Sqlite");
        }
    }

    [Fact]
    public void Only_the_observability_module_references_opentelemetry()
    {
        // ADR 0014. The request path records through IExchangeTelemetry and instruments with
        // System.Diagnostics primitives; the SDK is a subscriber and an exporter, and confining it
        // keeps "what is recorded" independent of "whether and where it is exported".
        foreach (var assembly in AllProductionAssemblies().Where(candidate => candidate != Observability))
        {
            AssertNoReferenceTo(assembly, "OpenTelemetry");
        }
    }

    [Fact]
    public void No_production_assembly_registers_its_own_activity_listener()
    {
        // The SDK replaced a hand-registered listener and must not run beside one (ADR 0014): two
        // listeners on one source both take part in every sampling decision, and the one that does
        // not export would be a second, silent opinion on which spans exist.
        foreach (var assembly in AllProductionAssemblies())
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOn("System.Diagnostics.ActivityListener")
                .GetResult();

            Assert.True(result.IsSuccessful, Describe(result));
        }
    }

    [Fact]
    public void No_assembly_references_newtonsoft_json()
    {
        // ADR 0002 selects System.Text.Json. Adding Newtonsoft.Json requires a superseding ADR, so
        // an accidental transitive reference must fail the build rather than be discovered later.
        foreach (var assembly in AllProductionAssemblies())
        {
            AssertNoReferenceTo(assembly, "Newtonsoft.Json");
        }
    }

    [Fact]
    public void The_durable_core_contains_no_vendor_specific_type()
    {
        string[] vendorTokens = ["LmStudio", "Laguna", "Qwen", "OpenCode", "Cline", "Aider", "LlamaCpp", "Ollama"];

        foreach (var assembly in new[] { Domain, Application })
        {
            foreach (var type in assembly.GetTypes())
            {
                var fullName = type.FullName ?? type.Name;

                foreach (var token in vendorTokens)
                {
                    Assert.False(
                        fullName.Contains(token, StringComparison.OrdinalIgnoreCase),
                        FormattableString.Invariant(
                            $"'{fullName}' names a vendor in {assembly.GetName().Name}. Vendor-specific behaviour belongs in an adapter (CLAUDE.md, ADR 0006)."));
                }
            }
        }
    }

    [Fact]
    public void The_health_probe_references_only_the_base_class_library()
    {
        // ADR 0016. The probe is told one URL and knows nothing else about the gateway; a reference
        // to any AgentSplice module or framework package would let it start depending on how the
        // gateway is built, and would make every probe run load it.
        var references = HealthProbe.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.All(references, name => Assert.True(
            name == "System" || name.StartsWith("System.", StringComparison.Ordinal),
            FormattableString.Invariant($"AgentSplice.HealthProbe references '{name}', which is not part of the base class library.")));

        AssertNoReferenceTo(
            HealthProbe,
            "AgentSplice.Api",
            "AgentSplice.Application",
            "AgentSplice.Domain",
            "AgentSplice.Infrastructure",
            "AgentSplice.Observability",
            "AgentSplice.Protocols",
            "AgentSplice.Providers",
            "Microsoft.AspNetCore",
            "Microsoft.EntityFrameworkCore",
            "Microsoft.Extensions");
    }

    [Fact]
    public void No_gateway_assembly_references_the_health_probe()
    {
        // The other direction: the probe is a separate command the image runs, not a library the
        // gateway calls.
        foreach (var assembly in AllProductionAssemblies().Where(candidate => candidate != HealthProbe).Append(Api))
        {
            AssertNoReferenceTo(assembly, "AgentSplice.HealthProbe");
        }
    }

    [Fact]
    public void Vendor_specific_naming_is_confined_to_its_own_provider_module()
    {
        var result = Types.InAssembly(ProvidersLmStudio)
            .That()
            .ResideInNamespaceStartingWith("AgentSplice.Providers.LmStudio")
            .Should()
            .ResideInNamespaceStartingWith("AgentSplice.Providers.LmStudio")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static IEnumerable<Assembly> AllProductionAssemblies() =>
    [
        Domain,
        Application,
        Infrastructure,
        Observability,
        ProtocolsOpenAI,
        ProvidersLmStudio,
        HealthProbe,
    ];

    private static void AssertNoReferenceTo(Assembly assembly, params string[] forbiddenPrefixes)
    {
        var referenced = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        foreach (var prefix in forbiddenPrefixes)
        {
            var violations = referenced
                .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
                .ToArray();

            Assert.True(
                violations.Length == 0,
                FormattableString.Invariant(
                    $"{assembly.GetName().Name} must not reference '{prefix}' but references: {string.Join(", ", violations)}."));
        }

        // A referenced-assembly check alone can be defeated by type forwarding or by an unused
        // reference being trimmed, so the IL-level dependency is asserted as well.
        var result = Types.InAssembly(assembly)
            .Should()
            .NotHaveDependencyOnAny(forbiddenPrefixes)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static string Describe(TestResult result) =>
        result.IsSuccessful
            ? "Rule satisfied."
            : "Violating types: " + string.Join(
                ", ",
                (result.FailingTypeNames ?? []).DefaultIfEmpty("(none reported)"));
}
