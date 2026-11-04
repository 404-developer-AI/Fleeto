using System;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EndOfLifeReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EndOfLifeReports",
                table: "NotificationChannels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EndOfLifeExtensionRelease",
                table: "Endpoints",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EndOfLifeRefreshRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    HandledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: true),
                    Problem = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndOfLifeRefreshRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EndOfLifeReleases",
                columns: table => new
                {
                    Product = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Release = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EndOfLifeOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ExtensionUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    LatestBuild = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndOfLifeReleases", x => new { x.Product, x.Release });
                });

            migrationBuilder.CreateTable(
                name: "EndOfLifeReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Scheduled = table.Column<bool>(type: "boolean", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedByName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodMonths = table.Column<int>(type: "integer", nullable: false),
                    DataAsOf = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Notified = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndOfLifeReports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EndOfLifeSources",
                columns: table => new
                {
                    Product = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailingSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReleaseCount = table.Column<int>(type: "integer", nullable: false),
                    Problem = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndOfLifeSources", x => x.Product);
                });

            migrationBuilder.CreateTable(
                name: "EndOfLifeReportCounts",
                columns: table => new
                {
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    Endpoints = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndOfLifeReportCounts", x => new { x.ReportId, x.SiteId, x.Status });
                    table.ForeignKey(
                        name: "FK_EndOfLifeReportCounts_EndOfLifeReports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "EndOfLifeReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EndOfLifeReportCounts_Sites_SiteId_ClientId",
                        columns: x => new { x.SiteId, x.ClientId },
                        principalTable: "Sites",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EndOfLifeReportRows",
                columns: table => new
                {
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    OsName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Product = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Release = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ReleaseLabel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    EndOfLifeOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ExtensionUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    ExtensionCounted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndOfLifeReportRows", x => new { x.ReportId, x.EndpointId });
                    table.ForeignKey(
                        name: "FK_EndOfLifeReportRows_EndOfLifeReports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "EndOfLifeReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EndOfLifeReportRows_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EndOfLifeRefreshRequests_RequestedAt",
                table: "EndOfLifeRefreshRequests",
                column: "RequestedAt");

            migrationBuilder.CreateIndex(
                name: "IX_EndOfLifeReportCounts_ClientId_ReportId",
                table: "EndOfLifeReportCounts",
                columns: new[] { "ClientId", "ReportId" });

            migrationBuilder.CreateIndex(
                name: "IX_EndOfLifeReportCounts_SiteId_ClientId",
                table: "EndOfLifeReportCounts",
                columns: new[] { "SiteId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_EndOfLifeReportRows_ClientId_ReportId",
                table: "EndOfLifeReportRows",
                columns: new[] { "ClientId", "ReportId" });

            migrationBuilder.CreateIndex(
                name: "IX_EndOfLifeReportRows_EndpointId_ClientId",
                table: "EndOfLifeReportRows",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_EndOfLifeReports_CreatedAt",
                table: "EndOfLifeReports",
                column: "CreatedAt");

            migrationBuilder.Sql(EndOfLifeSql.RefreshUp);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(EndOfLifeSql.RefreshDown);

            migrationBuilder.DropTable(
                name: "EndOfLifeRefreshRequests");

            migrationBuilder.DropTable(
                name: "EndOfLifeReleases");

            migrationBuilder.DropTable(
                name: "EndOfLifeReportCounts");

            migrationBuilder.DropTable(
                name: "EndOfLifeReportRows");

            migrationBuilder.DropTable(
                name: "EndOfLifeSources");

            migrationBuilder.DropTable(
                name: "EndOfLifeReports");

            migrationBuilder.DropColumn(
                name: "EndOfLifeReports",
                table: "NotificationChannels");

            migrationBuilder.DropColumn(
                name: "EndOfLifeExtensionRelease",
                table: "Endpoints");
        }
    }
}
