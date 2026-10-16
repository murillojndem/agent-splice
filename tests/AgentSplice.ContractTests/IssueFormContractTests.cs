using System.Xml.Linq;
using AgentSplice.TestSupport;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace AgentSplice.ContractTests;

/// <summary>
/// The issue forms are ones GitHub can offer, and a bug report says which build was running (ADR 0020).
/// </summary>
/// <remarks>
/// An invalid form fails silently: GitHub does not offer it, and the chooser shows a blank issue
/// instead. The forms once carried <c>about</c>, the key of a Markdown template, where an issue form takes
/// <c>description</c>, so neither was ever offered.
/// </remarks>
public sealed class IssueFormContractTests
{
    [Fact]
    public void Every_issue_form_declares_what_github_requires()
    {
        var forms = Directory.GetFiles(RepositoryPaths.Resolve(".github", "ISSUE_TEMPLATE"), "*.yml")
            .Where(path => !string.Equals(Path.GetFileName(path), "config.yml", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(forms);

        foreach (var path in forms)
        {
            var name = Path.GetFileName(path);
            var form = Load(path);

            Assert.False(string.IsNullOrWhiteSpace(Scalar(form, "name")), FormattableString.Invariant($"{name} has no name."));
            Assert.False(string.IsNullOrWhiteSpace(Scalar(form, "description")), FormattableString.Invariant($"{name} has no description."));
            Assert.False(
                form.Children.ContainsKey(new YamlScalarNode("about")),
                FormattableString.Invariant($"{name} carries `about`, which belongs to Markdown templates; an issue form takes `description`."));

            var body = Assert.IsType<YamlSequenceNode>(form.Children[new YamlScalarNode("body")]).Children.Cast<YamlMappingNode>().ToArray();
            Assert.NotEmpty(body);

            foreach (var element in body)
            {
                AssertValidElement(name, element);
            }

            var labels = body
                .Where(element => Scalar(element, "type") != "markdown")
                .Select(element => Scalar(Attributes(element), "label"))
                .ToArray();

            Assert.Equal(labels.Length, labels.Distinct(StringComparer.Ordinal).Count());

            var ids = body.Select(element => Scalar(element, "id")).OfType<string>().ToArray();
            Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void A_bug_report_says_which_build_was_running()
    {
        var body = Assert.IsType<YamlSequenceNode>(Load(RepositoryPaths.Resolve(".github", "ISSUE_TEMPLATE", "bug_report.yml"))
            .Children[new YamlScalarNode("body")]).Children.Cast<YamlMappingNode>();

        foreach (var label in new[] { "AgentSplice version", "Installation" })
        {
            var field = Assert.Single(body, element =>
                element.Children.TryGetValue(new YamlScalarNode("attributes"), out var attributes)
                && Scalar((YamlMappingNode)attributes, "label") == label);

            var validations = Assert.IsType<YamlMappingNode>(field.Children[new YamlScalarNode("validations")]);
            Assert.Equal("true", Scalar(validations, "required"));
        }
    }

    [Fact]
    public void The_issue_chooser_sends_vulnerabilities_to_private_reporting()
    {
        var config = Load(RepositoryPaths.Resolve(".github", "ISSUE_TEMPLATE", "config.yml"));
        var links = Assert.IsType<YamlSequenceNode>(config.Children[new YamlScalarNode("contact_links")]).Children.Cast<YamlMappingNode>();

        var repository = Assert.Single(XDocument.Parse(RepositoryPaths.ReadText("Directory.Build.props")).Descendants("RepositoryUrl")).Value.Trim();

        Assert.Contains(links, link => Scalar(link, "url") == repository + "/security/advisories/new");
    }

    [Fact]
    public void The_issue_forms_apply_only_labels_github_creates()
    {
        // A form that names a label the repository lacks files the issue without it, silently. These are the
        // labels GitHub creates in every new repository.
        string[] defaults = ["bug", "documentation", "duplicate", "enhancement", "good first issue", "help wanted", "invalid", "question", "wontfix"];

        foreach (var path in Directory.GetFiles(RepositoryPaths.Resolve(".github", "ISSUE_TEMPLATE"), "*.yml")
            .Where(path => !string.Equals(Path.GetFileName(path), "config.yml", StringComparison.Ordinal)))
        {
            var form = Load(path);

            if (!form.Children.TryGetValue(new YamlScalarNode("labels"), out var labels))
            {
                continue;
            }

            foreach (var label in Assert.IsType<YamlSequenceNode>(labels).Children.Cast<YamlScalarNode>())
            {
                Assert.Contains(label.Value, defaults);
            }
        }
    }

    /// <summary>The rules of GitHub's form schema that, broken, make GitHub drop the whole form.</summary>
    private static void AssertValidElement(string form, YamlMappingNode element)
    {
        string[] types = ["checkboxes", "dropdown", "input", "markdown", "textarea", "upload"];

        var type = Scalar(element, "type");
        Assert.True(type is not null && types.Contains(type), FormattableString.Invariant($"{form} has an element of type '{type}', which GitHub does not know."));

        var attributes = Attributes(element);

        if (type == "markdown")
        {
            Assert.False(string.IsNullOrWhiteSpace(Scalar(attributes, "value")), FormattableString.Invariant($"{form} has a markdown element without a value."));
            return;
        }

        Assert.False(string.IsNullOrWhiteSpace(Scalar(attributes, "label")), FormattableString.Invariant($"{form} has a field without a label."));

        if (type is "dropdown" or "checkboxes")
        {
            var options = Assert.IsType<YamlSequenceNode>(attributes.Children[new YamlScalarNode("options")]).Children;
            var choices = options.Select(option => option is YamlMappingNode mapping ? Scalar(mapping, "label") : ((YamlScalarNode)option).Value).ToArray();

            Assert.NotEmpty(choices);
            Assert.Equal(choices.Length, choices.Distinct(StringComparer.Ordinal).Count());
        }

        if (Scalar(element, "id") is { } id)
        {
            Assert.Matches("^[A-Za-z0-9_-]+$", id);
        }
    }

    private static YamlMappingNode Attributes(YamlMappingNode element) =>
        Assert.IsType<YamlMappingNode>(element.Children[new YamlScalarNode("attributes")]);

    private static YamlMappingNode Load(string path)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(File.ReadAllText(path)));

        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static string? Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? (value as YamlScalarNode)?.Value : null;
}
