using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentSplice.ContractTests.Documents;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Measurements;
using AgentSplice.Domain.Observations;
using AgentSplice.TestSupport;
using Xunit;

namespace AgentSplice.ContractTests;

/// <summary>
/// Every trace report under <c>docs/reports</c> is a dated record of real sessions: its evidence is what
/// the administrative API answered, every value of its environment says where it came from, and nothing
/// in it carries what the sessions said or who ran them (ADR 0023).
/// </summary>
/// <remarks>
/// A report is written once, by hand, from a machine CI never sees, and published for anyone to read.
/// Nothing re-checks it after that, so the parts of it a program can check are checked here on every
/// build. What no program can check — that the export was complete when it was made, that a value was
/// not guessed under a plausible source, that a name in prose identifies nobody — stays the author's and
/// the reviewer's, and docs/reports/README.md says so.
/// </remarks>
public sealed partial class TraceReportContractTests
{
    private const string ReportFile = "README.md";
    private const string RecordFile = "report.json";
    private const string EvidenceFile = "exchanges.json";

    /// <summary>
    /// Keys under which an API that carried content would put it, in the protocols in scope and in the
    /// administrative API's own spelling. The evidence has none of them.
    /// </summary>
    private static readonly string[] ContentKeys =
    [
        "messages",
        "content",
        "prompt",
        "completion",
        "text",
        "arguments",
        "delta",
        "choices",
        "tool_calls",
        "toolCalls",
        "tools",
        "functions",
        "function",
        "reasoning",
        "reasoning_content",
        "refusal",
        "input",
        "instructions",
        "output",
        "body",
        "requestBody",
        "responseBody",
        "response_format",
        "json_schema",
        "stop",
        "metadata",
        "audio",
    ];

    /// <summary>How a stored timeline ends: with its persistence, or with its metadata queued when the completion stamp failed.</summary>
    private static readonly string[] LastObservations = ["persistence_completed", "metadata_queued"];

    /// <summary>The values a report's environment must give, as paths below <c>environment</c>.</summary>
    private static readonly string[] RequiredEnvironment =
    [
        "agentsplice.version",
        "agentsplice.commit",
        "agentsplice.package",
        "agentsplice.packageSha256",
        "agentsplice.configuration",
        "agentsplice.contentCapture",
        "machine.operatingSystem",
        "machine.gpuDriver",
        "runtime.name",
        "runtime.version",
        "runtime.engine",
        "client.name",
        "client.version",
        "client.invocation",
        "client.configuration",
    ];

    /// <summary>What every model a report used must give, from CONFORMANCE.md's reproducibility list.</summary>
    private static readonly string[] RequiredModel =
    [
        "id",
        "file",
        "sha256",
        "quantization",
        "contextLength",
        "gpuOffload",
        "parallel",
        "flashAttention",
        "kvCacheType",
        "speculativeDecoding",
    ];

    /// <summary>A report's files: nothing else is one.</summary>
    private static readonly string[] ReportFiles = [ReportFile, RecordFile, EvidenceFile];

    /// <summary>The sections every report has.</summary>
    private static readonly string[] ReportSections =
    [
        "## What was run",
        "## Exchanges",
        "## Findings",
        "## What this report does not show",
        "## Reproducing",
        "## Files",
    ];

    /// <summary>The evidence's top-level keys: the API's answers, and when the export was made.</summary>
    private static readonly string[] EvidenceKeys = ["exchanges", "exportedAt", "list", "system"];

    /// <summary>One exchange of the evidence: its detail and its timeline.</summary>
    private static readonly string[] ExchangeKeys = ["detail", "timeline"];

    /// <summary>The compatibility matrix's dimension columns.</summary>
    private static readonly string[] MatrixDimensions =
    [
        "Basic chat",
        "Streaming",
        "Native tools",
        "Adapter tools",
        "Replay",
        "Cache evidence",
    ];

    /// <summary>Where a word ends, for telling a path from part of a web address.</summary>
    private static readonly char[] TokenBoundaries = [' ', '\n', '\t', '"', '\'', '`', '(', '<', '['];

    /// <summary>Sources that name no source.</summary>
    private static readonly string[] PlaceholderSources = ["unknown", "todo", "tbd", "?", "-", "n/a", "none"];

    /// <summary>The only statuses a compatibility row can have while no conformance report exists.</summary>
    private static readonly string[] UntestedStatuses = ["Not tested", "Not implemented"];

    /// <summary>The only values a compatibility dimension can have while no conformance report exists.</summary>
    private static readonly string[] UntestedDimensions = ["Unknown", "Planned", "N/A"];

    public static TheoryData<string> Reports()
    {
        var reports = new TheoryData<string>();
        foreach (var report in ReportNames())
        {
            reports.Add(report);
        }

        return reports;
    }

    [Fact]
    public void At_least_one_real_client_and_runtime_trace_report_is_published()
    {
        // SPECIFICATION.md section 22, criterion 17: the first public alpha waits for one.
        Assert.NotEmpty(ReportNames());
    }

    [Fact]
    public void The_index_has_one_row_per_report_and_each_row_says_what_its_report_holds()
    {
        var rows = IndexRows().ToArray();

        Assert.Equal(ReportNames(), rows.Select(row => row.Report).Order(StringComparer.Ordinal));

        foreach (var (report, cells) in rows)
        {
            using var record = Read(report, RecordFile);
            var environment = record.RootElement.GetProperty("environment");

            Assert.Equal(report[..10], cells[0]);
            Assert.Equal(Value(Find(environment, "client.name")!.Value) + " " + Release(Value(Find(environment, "client.version")!.Value)), cells[1]);
            Assert.Equal(Value(Find(environment, "runtime.name")!.Value) + " " + Release(Value(Find(environment, "runtime.version")!.Value)), cells[2]);
            Assert.Equal(
                environment.GetProperty("models").EnumerateArray().Select(model => Value(model.GetProperty("id"))).Order(StringComparer.Ordinal),
                Backticked().Matches(cells[3]).Select(match => match.Groups["text"].Value).Order(StringComparer.Ordinal));
        }
    }

    [Theory]
    [MemberData(nameof(Reports))]
    public void A_report_is_three_files_named_for_its_client_runtime_and_first_session(string report)
    {
        Assert.Matches(ReportName(), report);

        var folder = RepositoryPaths.Resolve("docs", "reports", report);
        Assert.Empty(Directory.GetDirectories(folder));
        Assert.Equal(
            ReportFiles.Order(StringComparer.Ordinal),
            Directory.GetFiles(folder).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal));

        using var record = Read(report, RecordFile);
        using var evidence = Read(report, EvidenceFile);
        var root = record.RootElement;
        Assert.Equal(report, root.GetProperty("report").GetString());

        // Named for the client and the runtime, with a number after them for a second report of the same day.
        var environment = root.GetProperty("environment");
        var slug = Slug(Value(Find(environment, "client.name")!.Value)) + "-" + Slug(Value(Find(environment, "runtime.name")!.Value));
        var named = report[11..];
        Assert.True(
            named == slug || (named.StartsWith(slug + "-", StringComparison.Ordinal) && named[(slug.Length + 1)..].All(char.IsAsciiDigit)),
            FormattableString.Invariant($"{report} is not named <date>-{slug}, or <date>-{slug}-<n>."));

        // The time covered is exactly the attempts', dated in UTC by the first one, and exported after.
        var attempts = root.GetProperty("attempts").EnumerateArray().ToArray();
        var from = Instant(root.GetProperty("capturedFrom"));
        var until = Instant(root.GetProperty("capturedUntil"));

        Assert.Equal(attempts.Min(attempt => Instant(attempt.GetProperty("from"))), from);
        Assert.Equal(attempts.Max(attempt => Instant(attempt.GetProperty("until"))), until);
        Assert.Equal(report[..10], from.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Assert.True(Instant(evidence.RootElement.GetProperty("exportedAt")) >= until, "The evidence was exported before the sessions it covers ended.");
    }

    [Theory]
    [MemberData(nameof(Reports))]
    public void The_evidence_is_the_administrative_apis_own_answers(string report)
    {
        var openApi = OpenApiDocument.Load();
        var provenances = openApi.SchemaPropertyEnum("Measurement", "provenance");

        using var evidence = Read(report, EvidenceFile);
        var root = evidence.RootElement;

        // What the API answered, and the one value the export adds: when it was made.
        Assert.Equal(EvidenceKeys, root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

        var system = root.GetProperty("system");
        Assert.All(openApi.SchemaRequired("SystemInfo"), property => Assert.True(system.TryGetProperty(property, out _), FormattableString.Invariant($"system: {property}")));
        Assert.False(system.GetProperty("contentRetentionEnabled").GetBoolean(), "A report's gateway runs with content capture off.");

        // Every exchange the store listed is in the evidence, once, with its detail and its timeline.
        var listed = root.GetProperty("list").EnumerateArray()
            .SelectMany(page => page.GetProperty("items").EnumerateArray())
            .Select(item => item.GetProperty("exchangeId").GetString()!)
            .ToArray();
        var exchanges = Exchanges(evidence).ToArray();

        Assert.NotEmpty(exchanges);
        Assert.Null(root.GetProperty("list").EnumerateArray().Last().GetProperty("nextCursor").GetString());
        Assert.Equal(listed.Order(StringComparer.Ordinal), exchanges.Select(Identifier).Order(StringComparer.Ordinal));
        Assert.Equal(exchanges.Length, exchanges.Select(Identifier).Distinct(StringComparer.Ordinal).Count());

        foreach (var exchange in exchanges)
        {
            Assert.Equal(ExchangeKeys, exchange.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

            var detail = exchange.GetProperty("detail");
            var identifier = Identifier(exchange);

            Assert.All(openApi.SchemaRequired("ExchangeDetail"), property => Assert.True(detail.TryGetProperty(property, out _), FormattableString.Invariant($"{identifier}: {property}")));
            Assert.Contains(detail.GetProperty("status").GetString()!, WireNames.Of<ExchangeStatus>());
            Assert.Contains(detail.GetProperty("ingressProtocol").GetString()!, WireNames.Of<IngressProtocol>());
            Assert.Contains(detail.GetProperty("streamTermination").GetString()!, WireNames.Of<StreamTermination>());
            Assert.Equal("metadata_only", detail.GetProperty("contentRetentionState").GetString());

            if (detail.TryGetProperty("failureClass", out var failure) && failure.ValueKind == JsonValueKind.String)
            {
                Assert.Contains(failure.GetString()!, WireNames.Of<FailureClass>());
            }

            foreach (var measurement in detail.GetProperty("measurements").EnumerateArray())
            {
                Assert.All(openApi.SchemaRequired("Measurement"), property => Assert.True(measurement.TryGetProperty(property, out _), FormattableString.Invariant($"{identifier}: a measurement without {property}")));
                Assert.Contains(measurement.GetProperty("provenance").GetString(), provenances);
                Assert.Contains(measurement.GetProperty("unit").GetString()!, WireNames.Of<MeasurementUnit>());
            }

            // Usage keeps its provenance, component by component (CLAUDE.md).
            if (detail.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                foreach (var component in usage.EnumerateObject())
                {
                    Assert.True(component.Value.ValueKind == JsonValueKind.Object, FormattableString.Invariant($"{identifier}: usage {component.Name} has no provenance."));
                    Assert.Equal(JsonValueKind.Number, component.Value.GetProperty("value").ValueKind);
                    Assert.Contains(component.Value.GetProperty("provenance").GetString(), provenances);
                }
            }

            AssertTimeline(exchange.GetProperty("timeline").EnumerateArray().ToArray(), detail, identifier, openApi);
        }
    }

    [Theory]
    [MemberData(nameof(Reports))]
    public void The_evidence_holds_nothing_the_sessions_said(string report)
    {
        using var record = Read(report, RecordFile);
        using var evidence = Read(report, EvidenceFile);

        // No key of the evidence is one of the names an API puts content under.
        Assert.All(Keys(evidence.RootElement), key => Assert.DoesNotContain(key, ContentKeys, StringComparer.OrdinalIgnoreCase));

        // And no value carries the scenario's own words: any four consecutive words of its prompt, before
        // the marker, or a file it names, as a whole token. Anything else a value could carry is what the
        // author's marker search is for (docs/reports/README.md).
        var prompt = Value(record.RootElement.GetProperty("scenario").GetProperty("prompt"));
        var words = prompt[..(prompt.IndexOf("Session marker", StringComparison.Ordinal) is var marker and >= 0 ? marker : prompt.Length)]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var phrases = Enumerable.Range(0, Math.Max(0, words.Length - 3)).Select(start => string.Join(' ', words[start..(start + 4)])).ToArray();
        var files = FileName().Matches(prompt).Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        foreach (var value in Strings(evidence.RootElement))
        {
            foreach (var phrase in phrases)
            {
                Assert.False(value.Contains(phrase, StringComparison.OrdinalIgnoreCase), FormattableString.Invariant($"The evidence holds '{phrase}', from the scenario."));
            }

            foreach (var file in files)
            {
                Assert.False(
                    Regex.IsMatch(value, "(?<![A-Za-z0-9_.])" + Regex.Escape(file) + "(?![A-Za-z0-9_])", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)),
                    FormattableString.Invariant($"The evidence holds '{file}', a file the scenario names."));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Reports))]
    public void Every_exchange_belongs_to_one_attempt_inside_its_time_and_with_its_model(string report)
    {
        using var record = Read(report, RecordFile);
        using var evidence = Read(report, EvidenceFile);

        var attempts = record.RootElement.GetProperty("attempts").EnumerateArray().ToArray();
        var models = record.RootElement.GetProperty("environment").GetProperty("models").EnumerateArray()
            .Select(model => Value(model.GetProperty("id")))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(attempts.Length, attempts.Select(attempt => attempt.GetProperty("name").GetString()).Distinct(StringComparer.Ordinal).Count());

        var owners = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var attempt in attempts)
        {
            Assert.True(Instant(attempt.GetProperty("from")) <= Instant(attempt.GetProperty("until")), FormattableString.Invariant($"Attempt {attempt.GetProperty("name")} ends before it begins."));

            foreach (var exchange in attempt.GetProperty("exchanges").EnumerateArray())
            {
                Assert.True(owners.TryAdd(exchange.GetString()!, attempt), FormattableString.Invariant($"{exchange.GetString()} is in two attempts."));
            }
        }

        var recorded = Exchanges(evidence).Select(exchange => exchange.GetProperty("detail")).ToArray();
        Assert.Equal(owners.Keys.Order(StringComparer.Ordinal), recorded.Select(detail => detail.GetProperty("exchangeId").GetString()!).Order(StringComparer.Ordinal));

        foreach (var detail in recorded)
        {
            var identifier = detail.GetProperty("exchangeId").GetString()!;
            var attempt = owners[identifier];
            var model = attempt.GetProperty("model").GetString();

            Assert.InRange(Instant(detail.GetProperty("startedAt")), Instant(attempt.GetProperty("from")), Instant(attempt.GetProperty("until")));
            if (detail.TryGetProperty("completedAt", out var completed) && completed.ValueKind == JsonValueKind.String)
            {
                Assert.True(Instant(completed) <= Instant(attempt.GetProperty("until")), FormattableString.Invariant($"{identifier} completed after its attempt ended."));
            }

            // The model the client asked for — a model's id, or an alias — is the attempt's, and the one the
            // runtime served is one the environment describes.
            if (detail.TryGetProperty("clientModelId", out var requested) && requested.ValueKind == JsonValueKind.String)
            {
                Assert.Equal(model, requested.GetString());
            }

            if (detail.TryGetProperty("upstreamModelId", out var served) && served.ValueKind == JsonValueKind.String)
            {
                Assert.Contains(served.GetString()!, models);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Reports))]
    public void Every_value_of_the_environment_says_where_it_came_from(string report)
    {
        using var record = Read(report, RecordFile);
        using var evidence = Read(report, EvidenceFile);
        var environment = record.RootElement.GetProperty("environment");

        AssertSourced(environment, "environment");
        AssertSourced(record.RootElement.GetProperty("scenario"), "scenario");
        Assert.True(Find(record.RootElement.GetProperty("scenario"), "prompt") is not null, "The scenario gives no prompt.");

        foreach (var path in RequiredEnvironment)
        {
            Assert.True(Find(environment, path) is not null, FormattableString.Invariant($"The environment gives no {path}."));
        }

        var models = environment.GetProperty("models").EnumerateArray().ToArray();
        Assert.NotEmpty(models);
        foreach (var model in models)
        {
            Assert.All(RequiredModel, property => Assert.True(model.TryGetProperty(property, out _), FormattableString.Invariant($"A model gives no {property}.")));
            Assert.Matches(DigestOrUnknown(), Value(model.GetProperty("sha256")));
        }

        // The build the evidence came from, as the gateway itself reported it, checked by its digest.
        var system = evidence.RootElement.GetProperty("system");
        var version = Value(Find(environment, "agentsplice.version")!.Value);
        Assert.Equal(system.GetProperty("version").GetString(), version);

        var commit = Value(Find(environment, "agentsplice.commit")!.Value);
        Assert.Matches(Commit(), commit);
        if (version.Contains('+', StringComparison.Ordinal))
        {
            Assert.Equal(commit, version[(version.IndexOf('+', StringComparison.Ordinal) + 1)..]);
        }

        Assert.Matches(Digest(), Value(Find(environment, "agentsplice.packageSha256")!.Value));
        Assert.Equal(
            system.GetProperty("contentRetentionEnabled").GetBoolean(),
            Find(environment, "agentsplice.contentCapture")!.Value.GetProperty("value").GetBoolean());
    }

    [Theory]
    [MemberData(nameof(Reports))]
    public void The_report_names_its_build_its_exchanges_and_its_limits(string report)
    {
        var text = RepositoryPaths.ReadText("docs", "reports", report, ReportFile).Replace("\r\n", "\n", StringComparison.Ordinal);
        using var record = Read(report, RecordFile);
        using var evidence = Read(report, EvidenceFile);

        Assert.Contains("**This report makes no compatibility claim.**", text, StringComparison.Ordinal);
        foreach (var heading in ReportSections)
        {
            Assert.Contains("\n" + heading + "\n", text, StringComparison.Ordinal);
        }

        Assert.Contains(Value(Find(record.RootElement.GetProperty("environment"), "agentsplice.commit")!.Value), text, StringComparison.Ordinal);

        // No exchange goes unexplained: the exchanges table's Exchange column names each one, once, by the
        // start of its identifier.
        var section = text[text.IndexOf("\n## Exchanges\n", StringComparison.Ordinal)..];
        section = section[..(section.IndexOf("\n## ", 1, StringComparison.Ordinal) is var next and > 0 ? next : section.Length)];
        var (header, rows) = FirstTable(section);
        var column = Array.IndexOf(header, "Exchange");
        Assert.True(column >= 0, "The exchanges table has no Exchange column.");

        Assert.Equal(
            Exchanges(evidence).Select(exchange => "`" + Identifier(exchange)[..8] + "`").Order(StringComparer.Ordinal),
            rows.Select(row => row[column]).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Reports))]
    public void Nothing_in_a_report_names_a_person_or_a_machine_or_carries_a_secret_or_a_marker(string report)
    {
        using var record = Read(report, RecordFile);
        // A declared digest is the value of a key ending in "sha256", with or without a "sha256:" prefix.
        var digests = Sourced(record.RootElement)
            .Where(entry => entry.Path.EndsWith("sha256", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Value.GetProperty("value").ToString())
            .Select(value => value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? value["sha256:".Length..] : value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The account and machine that run the test, as whole words: on the author's own machine, before
        // the report is committed, that is the name most likely to have been copied in. Not in CI, whose
        // accounts are words such as "runner".
        var local = Environment.GetEnvironmentVariable("CI") is null
            ? new[] { Environment.UserName, Environment.MachineName }.Where(name => name.Length >= 4).ToArray()
            : [];

        foreach (var file in ReportFiles)
        {
            var text = RepositoryPaths.ReadText("docs", "reports", report, file);
            var where = report + "/" + file;

            foreach (var path in HomePaths(text))
            {
                Assert.Fail(FormattableString.Invariant($"{where} holds a path under a home directory: {path}"));
            }

            Assert.All(local, name => Assert.False(
                Regex.IsMatch(text, @"\b" + Regex.Escape(name) + @"\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)),
                FormattableString.Invariant($"{where} holds this machine's account or machine name.")));
            Assert.DoesNotMatch(BearerToken(), text);

            // A 64-digit hexadecimal run is a digest the report declares, or it is a secret.
            foreach (Match run in HexRun().Matches(text))
            {
                Assert.True(digests.Contains(run.Value), FormattableString.Invariant($"{where} holds a 64-digit value that is no declared digest."));
            }

            // A session's marker in the report would make the check that it reached nowhere meaningless.
            Assert.DoesNotMatch(SessionMarker(), text);
        }
    }

    [Fact]
    public void The_compatibility_matrix_holds_only_untested_values_while_no_conformance_report_exists()
    {
        // A trace report is evidence for a row, never the verification of one (docs/CONFORMANCE.md). Only a
        // conformance report can give a row a status that claims anything, and none exists: when one does,
        // this test has to say which rows it supports.
        var (header, rows) = FirstTable(RepositoryPaths.ReadText("docs", "COMPATIBILITY_MATRIX.md"));
        var status = Array.IndexOf(header, "Status");
        var dimensions = MatrixDimensions
            .Select(name => Array.IndexOf(header, name))
            .ToArray();
        Assert.True(status >= 0 && dimensions.All(index => index >= 0), "The matrix's columns changed; this test has to follow them.");

        foreach (var row in rows)
        {
            Assert.Contains(row[status], UntestedStatuses);
            Assert.All(dimensions, index => Assert.Contains(row[index], UntestedDimensions));
        }
    }

    private static void AssertTimeline(JsonElement[] timeline, JsonElement detail, string identifier, OpenApiDocument openApi)
    {
        var required = openApi.SchemaRequired("TimelineObservation");

        // From the request's acceptance, at the exchange's start, to its persistence — or to the queueing of
        // its metadata, where a stored timeline ends when the completion stamp could not be written. The
        // timestamps are not required to rise: the gateway keeps a clock's step backwards visible rather
        // than clamping it, and a report says what one means.
        Assert.Equal("request_accepted", timeline[0].GetProperty("type").GetString());
        Assert.Equal(Instant(detail.GetProperty("startedAt")), Instant(timeline[0].GetProperty("timestamp")));
        Assert.Contains(timeline[^1].GetProperty("type").GetString(), LastObservations);

        for (var index = 0; index < timeline.Length; index++)
        {
            var observation = timeline[index];
            Assert.All(required, property => Assert.True(observation.TryGetProperty(property, out _), FormattableString.Invariant($"{identifier}, observation {index}: {property}")));
            Assert.Equal(index, observation.GetProperty("sequence").GetInt32());
            Assert.Contains(observation.GetProperty("type").GetString()!, WireNames.Of<ObservationType>());
            Assert.Contains(observation.GetProperty("source").GetString()!, WireNames.Of<ObservationSource>());
            Instant(observation.GetProperty("timestamp"));
        }
    }

    /// <summary>
    /// Every object below the environment is a value with its source, or holds only such values, or
    /// lists of them; no value is empty, no source is a placeholder, and an unknown value says why.
    /// </summary>
    private static void AssertSourced(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object when element.TryGetProperty("value", out var value):
                var source = element.TryGetProperty("source", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString()!.Trim() : string.Empty;
                Assert.False(source.Length == 0, FormattableString.Invariant($"{path} gives a value without its source."));
                Assert.DoesNotContain(source, PlaceholderSources, StringComparer.OrdinalIgnoreCase);

                Assert.False(value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined, FormattableString.Invariant($"{path} has no value."));
                if (value.ValueKind == JsonValueKind.String)
                {
                    Assert.False(string.IsNullOrWhiteSpace(value.GetString()), FormattableString.Invariant($"{path} has an empty value."));

                    // "unknown" is a finding, and the source says why nothing reported it.
                    Assert.True(
                        !string.Equals(value.GetString(), "unknown", StringComparison.OrdinalIgnoreCase) || source.Length >= 20,
                        FormattableString.Invariant($"{path} is unknown, and its source does not say why."));
                }

                break;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    AssertSourced(property.Value, path + "." + property.Name);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AssertSourced(item, path + "[]");
                }

                break;

            default:
                Assert.Fail(FormattableString.Invariant($"{path} is a bare value; give it as {{ \"value\", \"source\" }}."));
                break;
        }
    }

    /// <summary>Every <c>{ "value", "source" }</c> pair in a document, with its path.</summary>
    private static IEnumerable<(string Path, JsonElement Value)> Sourced(JsonElement element, string path = "")
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("value", out _) && element.TryGetProperty("source", out _))
        {
            yield return (path, element);
            yield break;
        }

        var children = element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().Select(property => (Path: path + "." + property.Name, Element: property.Value)).ToArray(),
            JsonValueKind.Array => element.EnumerateArray().Select(item => (Path: path, Element: item)).ToArray(),
            _ => [],
        };

        foreach (var (childPath, child) in children)
        {
            foreach (var entry in Sourced(child, childPath))
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// Paths under a home directory: <c>Users\name</c> or <c>Users/name</c> with or without a drive,
    /// and <c>/home/name</c> — but not an account segment of a web address.
    /// </summary>
    private static IEnumerable<string> HomePaths(string text)
    {
        foreach (Match match in HomeSegment().Matches(text))
        {
            // A users segment inside a web address names an account on a site, not a directory on a machine.
            var start = text.LastIndexOfAny(TokenBoundaries, match.Index) + 1;
            var before = text[start..match.Index];
            if (!before.Contains("http://", StringComparison.OrdinalIgnoreCase) && !before.Contains("https://", StringComparison.OrdinalIgnoreCase))
            {
                yield return match.Value;
            }
        }
    }

    /// <summary>The index's rows, by the report they link to, as trimmed cells.</summary>
    private static IEnumerable<(string Report, string[] Cells)> IndexRows()
    {
        foreach (var cells in FirstTable(RepositoryPaths.ReadText("docs", "reports", "README.md")).Rows)
        {
            var link = IndexLink().Match(cells[^1]);
            Assert.True(link.Success, FormattableString.Invariant($"An index row links to no report: {string.Join(" | ", cells)}"));
            yield return (link.Groups["report"].Value, cells);
        }
    }

    /// <summary>The first Markdown table in a text: its header's cells, and each row's, trimmed.</summary>
    private static (string[] Header, string[][] Rows) FirstTable(string text)
    {
        var table = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .SkipWhile(line => !line.StartsWith('|'))
            .TakeWhile(line => line.StartsWith('|'))
            .ToArray();
        Assert.True(table.Length >= 2, "There is no table where one is expected.");

        return (Cells(table[0]), table.Skip(2).Select(Cells).ToArray());
    }

    private static string[] Cells(string row) => row.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();

    private static JsonElement? Find(JsonElement element, string path)
    {
        foreach (var name in path.Split('.'))
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return null;
            }
        }

        return element;
    }

    private static string Value(JsonElement sourced) => sourced.GetProperty("value").ToString();

    /// <summary>A version without its build metadata.</summary>
    private static string Release(string version) => version.Split('+')[0];

    private static string Slug(string name) => NonSlug().Replace(name.ToLowerInvariant(), "-").Trim('-');

    private static IEnumerable<string> Keys(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(property => Keys(property.Value).Prepend(property.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Keys),
        _ => [],
    };

    private static IEnumerable<string> Strings(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(property => Strings(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Strings),
        JsonValueKind.String => [element.GetString()!],
        _ => [],
    };

    private static JsonElement[] Exchanges(JsonDocument evidence) =>
        evidence.RootElement.GetProperty("exchanges").EnumerateArray().ToArray();

    private static string Identifier(JsonElement exchange) =>
        exchange.GetProperty("detail").GetProperty("exchangeId").GetString()!;

    /// <summary>A timestamp with its offset stated: one without could be read in any time zone.</summary>
    private static DateTimeOffset Instant(JsonElement value)
    {
        var text = value.GetString()!;
        Assert.Matches(ExplicitOffset(), text);

        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None);
    }

    private static JsonDocument Read(string report, string file) =>
        JsonDocument.Parse(RepositoryPaths.ReadText("docs", "reports", report, file));

    private static string[] ReportNames() =>
        Directory.GetDirectories(RepositoryPaths.Resolve("docs", "reports"))
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}(-[a-z0-9]+)+$")]
    private static partial Regex ReportName();

    [GeneratedRegex(@"\]\((?<report>[^)/]+)/README\.md\)")]
    private static partial Regex IndexLink();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    [GeneratedRegex(@"(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex ExplicitOffset();

    [GeneratedRegex(@"^[0-9a-f]{40}$")]
    private static partial Regex Commit();

    [GeneratedRegex(@"^[0-9a-f]{64}$")]
    private static partial Regex Digest();

    [GeneratedRegex(@"^([0-9a-f]{64}|unknown)$")]
    private static partial Regex DigestOrUnknown();

    [GeneratedRegex(@"(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])")]
    private static partial Regex HexRun();

    /// <summary>A file name with a source or text extension, as a whole token — not "e.g" or "stream.c" inside a longer name.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_.])[A-Za-z_][A-Za-z0-9_-]*\.(?:py|js|jsx|ts|tsx|cs|json|md|txt|go|rs|java|rb|c|h|cc|cpp|hpp|yml|yaml|toml|sh|ps1)(?![A-Za-z0-9_])")]
    private static partial Regex FileName();

    /// <summary>A users directory, in any case, with or without a drive, or a home directory, followed by a name.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_])users[\\/]+[^\\/\s""'`<>|]+|/home/[^\\/\s""'`<>|]+", RegexOptions.IgnoreCase)]
    private static partial Regex HomeSegment();

    [GeneratedRegex("`(?<text>[^`]+)`")]
    private static partial Regex Backticked();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/=-]{16,}")]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"trace-canary-[0-9a-f]{6,}")]
    private static partial Regex SessionMarker();
}
