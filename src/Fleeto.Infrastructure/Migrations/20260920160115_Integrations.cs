using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <summary>
    /// Integrations (0.4.0 step 1), additive. One row per external product an instance talks to, with its credentials as
    /// ciphertext bound to the row, and a mapping per tenant: an Action1 organization maps to exactly one client and a
    /// client to at most one organization, both enforced by a unique index. Deleting a client removes its mapping.
    /// The inventory of an endpoint gains the id of the Action1 agent installed on it, which is how patch state is matched
    /// to an endpoint instead of by host name.
    /// </summary>
    public partial class Integrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Action1AgentId",
                table: "InventorySnapshots",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "Integrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Region = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    EncryptedCredentials = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    CredentialName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StatusMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Integrations", x => x.Id);
                    table.CheckConstraint("CK_Integrations_Action1", "\"Type\" <> 'Action1' OR (\"Region\" IS NOT NULL AND \"EncryptedCredentials\" <> '')");
                });

            migrationBuilder.CreateTable(
                name: "IntegrationMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalTenantId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExternalTenantName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IntegrationMappings_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IntegrationMappings_Integrations_IntegrationId",
                        column: x => x.IntegrationId,
                        principalTable: "Integrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventorySnapshots_Action1AgentId",
                table: "InventorySnapshots",
                column: "Action1AgentId",
                filter: "\"Action1AgentId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationMappings_ClientId",
                table: "IntegrationMappings",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationMappings_IntegrationId_ClientId",
                table: "IntegrationMappings",
                columns: new[] { "IntegrationId", "ClientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationMappings_IntegrationId_ExternalTenantId",
                table: "IntegrationMappings",
                columns: new[] { "IntegrationId", "ExternalTenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Integrations_Type",
                table: "Integrations",
                column: "Type",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntegrationMappings");

            migrationBuilder.DropTable(
                name: "Integrations");

            migrationBuilder.DropIndex(
                name: "IX_InventorySnapshots_Action1AgentId",
                table: "InventorySnapshots");

            migrationBuilder.DropColumn(
                name: "Action1AgentId",
                table: "InventorySnapshots");
        }
    }
}
