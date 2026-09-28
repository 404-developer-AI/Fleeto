using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InstanceHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "InstanceHealth",
                table: "NotificationChannels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "InstanceHealthIssues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Component = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanceHealthIssues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InstanceHealthSamples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CpuPercent = table.Column<double>(type: "double precision", nullable: true),
                    Load1 = table.Column<double>(type: "double precision", nullable: true),
                    Cores = table.Column<int>(type: "integer", nullable: true),
                    MemoryTotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    MemoryAvailableBytes = table.Column<long>(type: "bigint", nullable: true),
                    SwapTotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    SwapFreeBytes = table.Column<long>(type: "bigint", nullable: true),
                    DiskTotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    DiskFreeBytes = table.Column<long>(type: "bigint", nullable: true),
                    DatabaseBytes = table.Column<long>(type: "bigint", nullable: false),
                    DatabaseConnections = table.Column<int>(type: "integer", nullable: false),
                    DatabaseMaxConnections = table.Column<int>(type: "integer", nullable: false),
                    DetailJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanceHealthSamples", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InstanceHealthIssues_OpenKey",
                table: "InstanceHealthIssues",
                column: "Key",
                unique: true,
                filter: "\"ResolvedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InstanceHealthIssues_ResolvedAt",
                table: "InstanceHealthIssues",
                column: "ResolvedAt");

            migrationBuilder.CreateIndex(
                name: "IX_InstanceHealthSamples_Time",
                table: "InstanceHealthSamples",
                column: "Time");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InstanceHealthIssues");

            migrationBuilder.DropTable(
                name: "InstanceHealthSamples");

            migrationBuilder.DropColumn(
                name: "InstanceHealth",
                table: "NotificationChannels");
        }
    }
}
