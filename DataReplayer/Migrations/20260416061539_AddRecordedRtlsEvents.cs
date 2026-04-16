using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DataReplayer.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordedRtlsEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecordedEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Endpoint = table.Column<string>(type: "text", nullable: false),
                    TrackerId = table.Column<string>(type: "text", nullable: true),
                    Payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordedEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecordedRtlsEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UwbMacAddress = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RawPayload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordedRtlsEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TrackersWhiteList = table.Column<string>(type: "text", nullable: false),
                    SubscribedTopics = table.Column<string>(type: "text", nullable: false),
                    RetentionHours = table.Column<int>(type: "integer", nullable: false),
                    IsRecordingEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    TrackerIdTopicSegmentIndex = table.Column<int>(type: "integer", nullable: false),
                    MqttBrokerHost = table.Column<string>(type: "text", nullable: false),
                    MqttBrokerPort = table.Column<int>(type: "integer", nullable: false),
                    MqttUsername = table.Column<string>(type: "text", nullable: true),
                    MqttPassword = table.Column<string>(type: "text", nullable: true),
                    RtlsWebSocketUrl = table.Column<string>(type: "text", nullable: false),
                    IsRtlsRecordingEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecordedEvents_ReceivedAt",
                table: "RecordedEvents",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RecordedEvents_TrackerId",
                table: "RecordedEvents",
                column: "TrackerId");

            migrationBuilder.CreateIndex(
                name: "IX_RecordedRtlsEvents_ReceivedAt",
                table: "RecordedRtlsEvents",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RecordedRtlsEvents_UwbMacAddress",
                table: "RecordedRtlsEvents",
                column: "UwbMacAddress");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecordedEvents");

            migrationBuilder.DropTable(
                name: "RecordedRtlsEvents");

            migrationBuilder.DropTable(
                name: "Settings");
        }
    }
}
