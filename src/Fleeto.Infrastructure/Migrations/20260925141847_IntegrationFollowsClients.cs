using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class IntegrationFollowsClients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FollowClients",
                table: "Integrations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "FollowMessage",
                table: "Integrations",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SyncedName",
                table: "IntegrationMappings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            // A mapping made by hand is in step with the client as it is today, so switching on "follow clients" never
            // renames an organization that nobody renamed in Fleeto.
            migrationBuilder.Sql("""
                UPDATE "IntegrationMappings" AS m SET "SyncedName" = c."Name"
                FROM "Clients" AS c WHERE c."Id" = m."ClientId";
                """);

            migrationBuilder.CreateTable(
                name: "IntegrationOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TargetClientId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExternalTenantId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExternalGroupId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExternalEndpointId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IntegrationOperations_Clients_TargetClientId",
                        column: x => x.TargetClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IntegrationOperations_Integrations_IntegrationId",
                        column: x => x.IntegrationId,
                        principalTable: "Integrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationSiteGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalTenantId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExternalGroupId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SyncedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MembersHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MembersSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationSiteGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IntegrationSiteGroups_Integrations_IntegrationId",
                        column: x => x.IntegrationId,
                        principalTable: "Integrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IntegrationSiteGroups_Sites_SiteId_ClientId",
                        columns: x => new { x.SiteId, x.ClientId },
                        principalTable: "Sites",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationOperations_IntegrationId_NextAttemptAt",
                table: "IntegrationOperations",
                columns: new[] { "IntegrationId", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationOperations_TargetClientId",
                table: "IntegrationOperations",
                column: "TargetClientId");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationSiteGroups_ClientId",
                table: "IntegrationSiteGroups",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationSiteGroups_IntegrationId_SiteId",
                table: "IntegrationSiteGroups",
                columns: new[] { "IntegrationId", "SiteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationSiteGroups_SiteId_ClientId",
                table: "IntegrationSiteGroups",
                columns: new[] { "SiteId", "ClientId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntegrationOperations");

            migrationBuilder.DropTable(
                name: "IntegrationSiteGroups");

            migrationBuilder.DropColumn(
                name: "FollowClients",
                table: "Integrations");

            migrationBuilder.DropColumn(
                name: "FollowMessage",
                table: "Integrations");

            migrationBuilder.DropColumn(
                name: "SyncedName",
                table: "IntegrationMappings");
        }
    }
}
