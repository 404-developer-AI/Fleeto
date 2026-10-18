using System;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PerformanceDiagnose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Statements",
                table: "PerfEvents",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ActiveSessions",
                table: "PerfDatabaseSnapshots",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IdleInTransactionSessions",
                table: "PerfDatabaseSnapshots",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IdleSessions",
                table: "PerfDatabaseSnapshots",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LockWaitingSessions",
                table: "PerfDatabaseSnapshots",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LongestIdleInTransactionSeconds",
                table: "PerfDatabaseSnapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LongestLockWaitSeconds",
                table: "PerfDatabaseSnapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LongestTransactionSeconds",
                table: "PerfDatabaseSnapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PerfFingerprints",
                columns: table => new
                {
                    Component = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Hash = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Sql = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Callers = table.Column<string[]>(type: "text[]", nullable: false),
                    FirstSeen = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeen = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerfFingerprints", x => new { x.Component, x.Hash });
                });

            migrationBuilder.CreateIndex(
                name: "IX_PerfFingerprints_Hash",
                table: "PerfFingerprints",
                column: "Hash");

            migrationBuilder.CreateIndex(
                name: "IX_PerfFingerprints_LastSeen",
                table: "PerfFingerprints",
                column: "LastSeen");

            migrationBuilder.Sql(PerformanceSql.DiagnoseUp);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PerformanceSql.DiagnoseDown);

            migrationBuilder.DropTable(
                name: "PerfFingerprints");

            migrationBuilder.DropColumn(
                name: "Statements",
                table: "PerfEvents");

            migrationBuilder.DropColumn(
                name: "ActiveSessions",
                table: "PerfDatabaseSnapshots");

            migrationBuilder.DropColumn(
                name: "IdleInTransactionSessions",
                table: "PerfDatabaseSnapshots");

            migrationBuilder.DropColumn(
                name: "IdleSessions",
                table: "PerfDatabaseSnapshots");

            migrationBuilder.DropColumn(
                name: "LockWaitingSessions",
                table: "PerfDatabaseSnapshots");

            migrationBuilder.DropColumn(
                name: "LongestIdleInTransactionSeconds",
                table: "PerfDatabaseSnapshots");

            migrationBuilder.DropColumn(
                name: "LongestLockWaitSeconds",
                table: "PerfDatabaseSnapshots");

            migrationBuilder.DropColumn(
                name: "LongestTransactionSeconds",
                table: "PerfDatabaseSnapshots");
        }
    }
}
