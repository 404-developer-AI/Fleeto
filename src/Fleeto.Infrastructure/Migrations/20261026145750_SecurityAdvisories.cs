using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SecurityAdvisories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AdvisoriesSeenAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AdvisorySources",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailingSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Problem = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdvisorySources", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Advisories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Cves = table.Column<string[]>(type: "text[]", nullable: false),
                    Link = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Advisories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Advisories_AdvisorySources_Source",
                        column: x => x.Source,
                        principalTable: "AdvisorySources",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AdvisoryHandlings",
                columns: table => new
                {
                    AdvisoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    HandledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdvisoryHandlings", x => x.AdvisoryId);
                    table.ForeignKey(
                        name: "FK_AdvisoryHandlings_Advisories_AdvisoryId",
                        column: x => x.AdvisoryId,
                        principalTable: "Advisories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Advisories_PublishedAt",
                table: "Advisories",
                column: "PublishedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Advisories_Source_SourceId",
                table: "Advisories",
                columns: new[] { "Source", "SourceId" },
                unique: true);

            // The sources of 0.8.0, on by default (VULNERABILITIES.md §2).
            migrationBuilder.Sql("""
                INSERT INTO "AdvisorySources" ("Key", "Enabled") VALUES ('ccb', true) ON CONFLICT ("Key") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdvisoryHandlings");

            migrationBuilder.DropTable(
                name: "Advisories");

            migrationBuilder.DropTable(
                name: "AdvisorySources");

            migrationBuilder.DropColumn(
                name: "AdvisoriesSeenAt",
                table: "AspNetUsers");
        }
    }
}
