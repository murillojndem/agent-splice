using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentSplice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialMetadataStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "exchanges",
                columns: table => new
                {
                    ExchangeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PublicRequestId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TraceId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    IngressProtocol = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAtTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    ClientModelId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    RuntimeEndpointId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    UpstreamModelId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ResolutionSource = table.Column<int>(type: "INTEGER", nullable: true),
                    ResolutionAliasId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Streaming = table.Column<bool>(type: "INTEGER", nullable: true),
                    StreamedResponse = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    FailureClass = table.Column<int>(type: "INTEGER", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    StreamTermination = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentRetentionState = table.Column<int>(type: "INTEGER", nullable: false),
                    EnvironmentSnapshotId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    UpstreamStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    UpstreamMediaType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    UpstreamRequestId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    RequestSummaryJson = table.Column<string>(type: "TEXT", nullable: true),
                    ResponseSummaryJson = table.Column<string>(type: "TEXT", nullable: true),
                    UsageJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exchanges", x => x.ExchangeId);
                });

            migrationBuilder.CreateTable(
                name: "exchange_measurements",
                columns: table => new
                {
                    MeasurementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExchangeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Value = table.Column<double>(type: "REAL", nullable: false),
                    Unit = table.Column<int>(type: "INTEGER", nullable: false),
                    Provenance = table.Column<int>(type: "INTEGER", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: true),
                    StartedAtTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    EndedAtTicks = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exchange_measurements", x => x.MeasurementId);
                    table.ForeignKey(
                        name: "FK_exchange_measurements_exchanges_ExchangeId",
                        column: x => x.ExchangeId,
                        principalTable: "exchanges",
                        principalColumn: "ExchangeId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "exchange_observations",
                columns: table => new
                {
                    ObservationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExchangeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    TimestampTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    Confidence = table.Column<double>(type: "REAL", nullable: true),
                    DetailsJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exchange_observations", x => x.ObservationId);
                    table.ForeignKey(
                        name: "FK_exchange_observations_exchanges_ExchangeId",
                        column: x => x.ExchangeId,
                        principalTable: "exchanges",
                        principalColumn: "ExchangeId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_exchange_measurements_ExchangeId",
                table: "exchange_measurements",
                column: "ExchangeId");

            migrationBuilder.CreateIndex(
                name: "IX_exchange_observations_ExchangeId_Sequence",
                table: "exchange_observations",
                columns: new[] { "ExchangeId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_exchanges_RuntimeEndpointId",
                table: "exchanges",
                column: "RuntimeEndpointId");

            migrationBuilder.CreateIndex(
                name: "IX_exchanges_StartedAtTicks_ExchangeId",
                table: "exchanges",
                columns: new[] { "StartedAtTicks", "ExchangeId" });

            migrationBuilder.CreateIndex(
                name: "IX_exchanges_Status",
                table: "exchanges",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "exchange_measurements");

            migrationBuilder.DropTable(
                name: "exchange_observations");

            migrationBuilder.DropTable(
                name: "exchanges");
        }
    }
}
