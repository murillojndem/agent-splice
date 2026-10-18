using System.Text;
using AgentSplice.Application.Configuration;
using AgentSplice.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentSplice.UnitTests.Configuration;

/// <summary>
/// A switch, a number, a duration, or a mode given as null fails startup and names the setting
/// (NFR 14.2, ADR 0024).
/// </summary>
/// <remarks>
/// Through JSON, because that is where a null comes from: every other test supplies settings in memory,
/// where nothing is written as null by accident, and .NET 10 changed what both the JSON provider and the
/// binder do with one. Bound by .NET 10 without this guard, <c>"mode": null</c> retains nothing and
/// raises no error at all.
/// </remarks>
public sealed class NullSettingValidatorTests
{
    [Theory]
    [InlineData("""{"agentsplice":{"persistence":{"mode":null}}}""", "agentsplice:persistence:mode")]
    [InlineData("""{"agentsplice":{"capture":{"metadataEnabled":null}}}""", "agentsplice:capture:metadataEnabled")]
    [InlineData("""{"agentsplice":{"capture":{"retention":{"sweepInterval":null}}}}""", "agentsplice:capture:retention:sweepInterval")]
    [InlineData("""{"agentsplice":{"diagnostics":{"storeHeaders":null}}}""", "agentsplice:diagnostics:storeHeaders")]
    [InlineData("""{"agentsplice":{"runtimes":[{"id":"lmstudio-local","enabled":null}]}}""", "agentsplice:runtimes:0:enabled")]
    [InlineData("""{"agentsplice":{"runtimes":[{"id":"lmstudio-local","timeouts":{"total":null}}]}}""", "agentsplice:runtimes:0:timeouts:total")]
    public void A_value_type_given_as_null_fails_startup_and_names_the_setting(string json, string path)
    {
        // Through the registration, so the guard is proved to be wired as well as right.
        var error = Assert.Throws<OptionsValidationException>(() => Bind(json));

        Assert.Contains(error.Failures, failure => failure.StartsWith(path + " is null.", StringComparison.Ordinal));
    }

    [Fact]
    public void Null_strings_sections_and_lists_are_not_refused()
    {
        // A null string is the setting's absence, and a null section or list keeps its defaults, as on
        // .NET 8. Only a value type loses its default to a null.
        var result = Validate(
            """
            {"agentsplice":{
              "publicBaseUrl":null,
              "persistence":{"connectionString":null},
              "dashboard":null,
              "aliases":null
            }}
            """);

        Assert.True(result.Succeeded, string.Join(" ", result.Failures ?? []));
    }

    [Fact]
    public void An_empty_section_or_list_is_not_a_null_setting()
    {
        // .NET 10 reads an empty object as null and an empty array as an empty string; neither is a
        // value the binder writes into a property.
        var result = Validate("""{"agentsplice":{"dashboard":{},"aliases":[],"runtimes":[]}}""");

        Assert.True(result.Succeeded, string.Join(" ", result.Failures ?? []));
    }

    [Fact]
    public void A_setting_with_a_value_is_not_refused()
    {
        var result = Validate("""{"agentsplice":{"persistence":{"mode":"None"},"capture":{"metadataEnabled":false}}}""");

        Assert.True(result.Succeeded);
    }

    private static AgentSpliceOptions Bind(string json)
    {
        var services = new ServiceCollection();
        services.AddAgentSpliceConfiguration(Configuration(json));

        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<IOptions<AgentSpliceOptions>>().Value;
    }

    private static ValidateOptionsResult Validate(string json) =>
        new NullSettingValidator(Configuration(json).GetSection(AgentSpliceOptions.SectionName))
            .Validate(Options.DefaultName, new AgentSpliceOptions());

    private static IConfiguration Configuration(string json) =>
        new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
}
