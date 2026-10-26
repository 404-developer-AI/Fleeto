using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Vulnerabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PackageInfoJson",
                table: "InventorySnapshots",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EndpointVulnerabilities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    Cve = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Package = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    InstalledVersion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FixedVersion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceSeverity = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Cvss = table.Column<decimal>(type: "numeric(3,1)", precision: 3, scale: 1, nullable: true),
                    Advisory = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Binaries = table.Column<string[]>(type: "text[]", nullable: false),
                    IsKernel = table.Column<bool>(type: "boolean", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndReason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndpointVulnerabilities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EndpointVulnerabilities_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EndpointVulnerabilitySummaries",
                columns: table => new
                {
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    Coverage = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Distribution = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Release = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Critical = table.Column<int>(type: "integer", nullable: false),
                    High = table.Column<int>(type: "integer", nullable: false),
                    Medium = table.Column<int>(type: "integer", nullable: false),
                    Low = table.Column<int>(type: "integer", nullable: false),
                    Unknown = table.Column<int>(type: "integer", nullable: false),
                    NoFix = table.Column<int>(type: "integer", nullable: false),
                    ProOnly = table.Column<int>(type: "integer", nullable: false),
                    NotOpen = table.Column<int>(type: "integer", nullable: false),
                    NotCovered = table.Column<int>(type: "integer", nullable: false),
                    RestartRequired = table.Column<bool>(type: "boolean", nullable: false),
                    PackageLists = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    PackageListsUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    InventoryHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FeedVersion = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    MatchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndpointVulnerabilitySummaries", x => x.EndpointId);
                    table.ForeignKey(
                        name: "FK_EndpointVulnerabilitySummaries_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VulnerabilityCves",
                columns: table => new
                {
                    Cve = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    UbuntuPriority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VulnerabilityCves", x => x.Cve);
                });

            migrationBuilder.CreateTable(
                name: "VulnerabilityFeeds",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailingSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Problem = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RecordCount = table.Column<int>(type: "integer", nullable: false),
                    ETag = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LastModified = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Scope = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VulnerabilityFeeds", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "VulnerabilityFeedRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    Feed = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Distribution = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Release = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Package = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Cve = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FixedVersion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ProService = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceSeverity = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Cvss = table.Column<decimal>(type: "numeric(3,1)", precision: 3, scale: 1, nullable: true),
                    Advisory = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsKernel = table.Column<bool>(type: "boolean", nullable: false),
                    KernelPattern = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ModuleStreams = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VulnerabilityFeedRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VulnerabilityFeedRecords_VulnerabilityFeeds_Feed",
                        column: x => x.Feed,
                        principalTable: "VulnerabilityFeeds",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EndpointVulnerabilities_ClientCve",
                table: "EndpointVulnerabilities",
                columns: new[] { "ClientId", "Cve" },
                filter: "\"EndedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EndpointVulnerabilities_EndedAt",
                table: "EndpointVulnerabilities",
                column: "EndedAt");

            migrationBuilder.CreateIndex(
                name: "IX_EndpointVulnerabilities_EndpointId_ClientId",
                table: "EndpointVulnerabilities",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_EndpointVulnerabilities_Open",
                table: "EndpointVulnerabilities",
                columns: new[] { "EndpointId", "Cve", "Package" },
                unique: true,
                filter: "\"EndedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EndpointVulnerabilitySummaries_ClientId",
                table: "EndpointVulnerabilitySummaries",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_EndpointVulnerabilitySummaries_EndpointId_ClientId",
                table: "EndpointVulnerabilitySummaries",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_VulnerabilityFeedRecords_Distribution_Release_Package",
                table: "VulnerabilityFeedRecords",
                columns: new[] { "Distribution", "Release", "Package" });

            migrationBuilder.CreateIndex(
                name: "IX_VulnerabilityFeedRecords_Feed",
                table: "VulnerabilityFeedRecords",
                column: "Feed");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EndpointVulnerabilities");

            migrationBuilder.DropTable(
                name: "EndpointVulnerabilitySummaries");

            migrationBuilder.DropTable(
                name: "VulnerabilityCves");

            migrationBuilder.DropTable(
                name: "VulnerabilityFeedRecords");

            migrationBuilder.DropTable(
                name: "VulnerabilityFeeds");

            migrationBuilder.DropColumn(
                name: "PackageInfoJson",
                table: "InventorySnapshots");
        }
    }
}
