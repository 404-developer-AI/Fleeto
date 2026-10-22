using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SecurityIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SecurityIncidents",
                table: "NotificationChannels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Times",
                table: "IpBlocks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "SecurityIncidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Network = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BlockedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedByName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AcknowledgeComment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedByName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ResolveComment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityIncidents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_Action_Time",
                table: "AuditEntries",
                columns: new[] { "Action", "Time" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityIncidents_OpenedAt",
                table: "SecurityIncidents",
                column: "OpenedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityIncidents_OpenKey",
                table: "SecurityIncidents",
                column: "Key",
                unique: true,
                filter: "\"ResolvedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityIncidents_ResolvedAt",
                table: "SecurityIncidents",
                column: "ResolvedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SecurityIncidents");

            migrationBuilder.DropIndex(
                name: "IX_AuditEntries_Action_Time",
                table: "AuditEntries");

            migrationBuilder.DropColumn(
                name: "SecurityIncidents",
                table: "NotificationChannels");

            migrationBuilder.DropColumn(
                name: "Times",
                table: "IpBlocks");
        }
    }
}
