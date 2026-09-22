using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PatchDeployments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PatchDeployments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalTenantId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExternalDeploymentId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AutoReboot = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StatusMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PolledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatchDeployments", x => x.Id);
                    table.UniqueConstraint("AK_PatchDeployments_Id_ClientId", x => new { x.Id, x.ClientId });
                    table.ForeignKey(
                        name: "FK_PatchDeployments_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatchDeploymentTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeploymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalEndpointId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatchDeploymentTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatchDeploymentTargets_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PatchDeploymentTargets_PatchDeployments_DeploymentId",
                        column: x => x.DeploymentId,
                        principalTable: "PatchDeployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatchDeploymentUpdates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeploymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalUpdateId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatchDeploymentUpdates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatchDeploymentUpdates_PatchDeployments_DeploymentId",
                        column: x => x.DeploymentId,
                        principalTable: "PatchDeployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PatchDeployments_BatchId",
                table: "PatchDeployments",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_PatchDeployments_ClientId_RequestedAt",
                table: "PatchDeployments",
                columns: new[] { "ClientId", "RequestedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_PatchDeployments_State",
                table: "PatchDeployments",
                column: "State",
                filter: "\"State\" IN ('Requested', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_PatchDeploymentTargets_DeploymentId_EndpointId",
                table: "PatchDeploymentTargets",
                columns: new[] { "DeploymentId", "EndpointId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatchDeploymentTargets_EndpointId_ClientId",
                table: "PatchDeploymentTargets",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_PatchDeploymentTargets_EndpointId_UpdatedAt",
                table: "PatchDeploymentTargets",
                columns: new[] { "EndpointId", "UpdatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_PatchDeploymentUpdates_DeploymentId",
                table: "PatchDeploymentUpdates",
                column: "DeploymentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PatchDeploymentTargets");

            migrationBuilder.DropTable(
                name: "PatchDeploymentUpdates");

            migrationBuilder.DropTable(
                name: "PatchDeployments");
        }
    }
}
