using System.Buffers;
using System.Text.Json;
using AgentSplice.Application.Administration;

namespace AgentSplice.Infrastructure.Administration;

/// <summary>
/// Encodes the administrative payloads declared in <c>openapi/agentsplice-openapi.yaml</c>.
/// </summary>
/// <remarks>
/// Written with <see cref="Utf8JsonWriter"/> rather than serialised from a model, for one reason that
/// decides it: the structural summaries are stored as JSON documents and are served through
/// unchanged. <c>ExchangeDetail.structuralSummary</c> is declared <c>additionalProperties: true</c>
/// precisely so a summary can gain a field without a schema change, and a serialiser would have to
/// deserialise and re-emit those documents — making this class the thing that has to keep up with
/// them, and quietly reformatting stored evidence on the way past.
///
/// Absent members are omitted rather than written as null wherever the schema allows it. A dashboard
/// reading this surface has to be able to tell "not measured" from "measured as zero"
/// (FR-DASH-006, FR-TRACE-006).
/// </remarks>
internal sealed class AdministrativeJsonWriter : IAdministrativeEnvelopeWriter
{
    /// <inheritdoc />
    public string MediaType => "application/json";

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Write(ExchangePageView page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("items");

            foreach (var item in page.Items)
            {
                WriteSummary(writer, item);
            }

            writer.WriteEndArray();

            // Present and null on the last page rather than absent, because "there are no more" is
            // an answer and a missing field is a client guessing at one.
            if (page.NextCursor is { } cursor)
            {
                writer.WriteString("nextCursor", cursor);
            }
            else
            {
                writer.WriteNull("nextCursor");
            }

            writer.WriteEndObject();
        });
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Write(ExchangeDetailView detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        return Build(writer =>
        {
            writer.WriteStartObject();
            WriteSummaryMembers(writer, detail.Summary);

            writer.WriteString("ingressProtocol", EnumWireNames.ToSnakeCase(detail.IngressProtocol));
            writer.WriteString("streamTermination", EnumWireNames.ToSnakeCase(detail.StreamTermination));

            WriteOptionalString(
                writer,
                "failureClass",
                detail.FailureClass is { } failure ? EnumWireNames.ToSnakeCase(failure) : null);

            WriteOptionalString(writer, "errorCode", detail.ErrorCode);

            if (detail.UpstreamStatusCode is { } status)
            {
                writer.WriteNumber("upstreamStatusCode", status);
            }

            writer.WriteStartArray("measurements");

            foreach (var measurement in detail.Measurements)
            {
                writer.WriteStartObject();
                writer.WriteString("name", measurement.Name);
                writer.WriteNumber("value", measurement.Value);
                writer.WriteString("unit", EnumWireNames.ToSnakeCase(measurement.Unit));
                writer.WriteString("provenance", EnumWireNames.ToSnakeCase(measurement.Provenance));

                if (measurement.Confidence is { } confidence)
                {
                    writer.WriteNumber("confidence", confidence);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            WriteStoredDocument(writer, "structuralSummary", detail.RequestSummaryJson);
            WriteStoredDocument(writer, "responseSummary", detail.ResponseSummaryJson);
            WriteStoredDocument(writer, "usage", detail.UsageJson);

            writer.WriteEndObject();
        });
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Write(IReadOnlyList<TimelineObservationView> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        return Build(writer =>
        {
            writer.WriteStartArray();

            foreach (var observation in observations)
            {
                writer.WriteStartObject();
                writer.WriteNumber("sequence", observation.Sequence);
                writer.WriteString("type", EnumWireNames.ToSnakeCase(observation.Type));
                writer.WriteString("timestamp", observation.Timestamp);
                writer.WriteString("source", EnumWireNames.ToSnakeCase(observation.Source));

                if (observation.Confidence is { } confidence)
                {
                    writer.WriteNumber("confidence", confidence);
                }

                WriteStoredDocument(writer, "safeDetails", observation.DetailsJson);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Write(SystemInfoView system)
    {
        ArgumentNullException.ThrowIfNull(system);

        return Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("version", system.Version);
            writer.WriteString("stage", system.Stage);

            writer.WriteStartArray("enabledModules");

            foreach (var module in system.EnabledModules)
            {
                writer.WriteStringValue(module);
            }

            writer.WriteEndArray();
            writer.WriteBoolean("contentRetentionEnabled", system.ContentRetentionEnabled);
            writer.WriteBoolean("metadataRetentionEnabled", system.MetadataRetentionEnabled);
            writer.WriteEndObject();
        });
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Write(IReadOnlyList<RuntimeSummaryView> runtimes)
    {
        ArgumentNullException.ThrowIfNull(runtimes);

        return Build(writer =>
        {
            writer.WriteStartArray();

            foreach (var runtime in runtimes)
            {
                writer.WriteStartObject();
                writer.WriteString("runtimeId", runtime.RuntimeId);
                writer.WriteString("provider", runtime.Provider);
                writer.WriteString("baseUrl", runtime.BaseUrl);

                // The name of a variable, never its value. Present-and-null says "this runtime uses
                // no credential", which is a different fact from "it uses one we will not name".
                if (runtime.ApiKeyEnvironmentVariable is { } variable)
                {
                    writer.WriteString("apiKeyEnvironmentVariable", variable);
                }
                else
                {
                    writer.WriteNull("apiKeyEnvironmentVariable");
                }

                writer.WriteBoolean("enabled", runtime.Enabled);
                writer.WriteBoolean("discoveryEnabled", runtime.DiscoveryEnabled);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Write(IReadOnlyList<CatalogModelView> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        return Build(writer =>
        {
            writer.WriteStartArray();

            foreach (var model in models)
            {
                writer.WriteStartObject();
                writer.WriteString("clientModelId", model.ClientModelId);
                writer.WriteString("runtimeId", model.RuntimeId);
                writer.WriteString("upstreamModelId", model.UpstreamModelId);
                WriteOptionalString(writer, "aliasId", model.AliasId);
                writer.WriteString("source", EnumWireNames.ToSnakeCase(model.Source));
                if (model.Reachable is { } reachable)
                {
                    writer.WriteBoolean("reachable", reachable);
                }
                else
                {
                    writer.WriteNull("reachable");
                }
                writer.WriteString(
                    "capabilityProvenance",
                    EnumWireNames.ToSnakeCase(model.CapabilityProvenance));

                // Absent rather than zero. /v1/models emits zero because the OpenAI schema demands a
                // present integer; that substitution belongs to the compatibility envelope alone, and
                // this is the surface that reports the honest absence (FR-DASH-006).
                if (model.Created is { } created)
                {
                    writer.WriteNumber("created", created);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Write(IReadOnlyList<RuntimeHealthView> health)
    {
        ArgumentNullException.ThrowIfNull(health);

        return Build(writer =>
        {
            writer.WriteStartArray();

            foreach (var runtime in health)
            {
                writer.WriteStartObject();
                writer.WriteString("runtimeId", runtime.RuntimeId);
                writer.WriteString("status", EnumWireNames.ToSnakeCase(runtime.Status));

                // Absent when nothing has consulted this runtime. A health surface that invented a
                // timestamp would make "never checked" indistinguishable from "checked and fine".
                if (runtime.CheckedAt is { } checkedAt)
                {
                    writer.WriteString("checkedAt", checkedAt);
                }

                writer.WriteBoolean("servedFromStaleCache", runtime.ServedFromStaleCache);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Write(ReadinessView readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);

        return Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ready", readiness.Ready);
            writer.WriteBoolean("requiresReachableRuntime", readiness.RequiresReachableRuntime);
            writer.WriteNumber("reachableRuntimes", readiness.ReachableRuntimes);
            writer.WriteNumber("enabledRuntimes", readiness.EnabledRuntimes);
            writer.WriteEndObject();
        });
    }

    private static void WriteSummary(Utf8JsonWriter writer, ExchangeSummaryView summary)
    {
        writer.WriteStartObject();
        WriteSummaryMembers(writer, summary);
        writer.WriteEndObject();
    }

    private static void WriteSummaryMembers(Utf8JsonWriter writer, ExchangeSummaryView summary)
    {
        writer.WriteString("exchangeId", summary.ExchangeId.ToString());
        writer.WriteString("requestId", summary.RequestId);
        WriteOptionalString(writer, "traceId", summary.TraceId);
        writer.WriteString("startedAt", summary.StartedAt);

        // Explicitly null while an exchange is unfinished or ended without an observed boundary. A
        // terminal status with a null completion time says "it ended and the moment was not
        // observed", which is the honest reading and needs the field to be present to say it.
        if (summary.CompletedAt is { } completedAt)
        {
            writer.WriteString("completedAt", completedAt);
        }
        else
        {
            writer.WriteNull("completedAt");
        }

        writer.WriteString("status", ExchangeQuery.WireName(summary.Status));
        WriteOptionalString(writer, "runtimeId", summary.RuntimeId);
        WriteOptionalString(writer, "clientModelId", summary.ClientModelId);
        WriteOptionalString(writer, "upstreamModelId", summary.UpstreamModelId);

        // Null when the request was refused before its envelope was read and therefore never stated
        // a preference. False would be a claim about a request nobody parsed.
        if (summary.Streaming is { } streaming)
        {
            writer.WriteBoolean("streaming", streaming);
        }
        else
        {
            writer.WriteNull("streaming");
        }

        writer.WriteString("contentRetentionState", EnumWireNames.ToSnakeCase(summary.ContentRetentionState));
    }

    /// <summary>Embeds a stored JSON document without reparsing it, or omits the member.</summary>
    private static void WriteStoredDocument(Utf8JsonWriter writer, string name, string? json)
    {
        if (json is null)
        {
            return;
        }

        writer.WritePropertyName(name);

        // Validated on the way out, cheaply, so a corrupted column cannot make this endpoint emit
        // a body that is not JSON at all.
        writer.WriteRawValue(json, skipInputValidation: false);
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    private static ReadOnlyMemory<byte> Build(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        return buffer.WrittenMemory;
    }
}
