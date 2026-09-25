using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PatchDeploymentSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "StepsReadAt",
                table: "PatchDeploymentTargets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_PatchDeploymentTargets_Id_ClientId",
                table: "PatchDeploymentTargets",
                columns: new[] { "Id", "ClientId" });

            migrationBuilder.CreateTable(
                name: "PatchDeploymentSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Operation = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Details = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatchDeploymentSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatchDeploymentSteps_PatchDeploymentTargets_TargetId_Client~",
                        columns: x => new { x.TargetId, x.ClientId },
                        principalTable: "PatchDeploymentTargets",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PatchDeploymentSteps_TargetId_ClientId",
                table: "PatchDeploymentSteps",
                columns: new[] { "TargetId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_PatchDeploymentSteps_TargetId_Position",
                table: "PatchDeploymentSteps",
                columns: new[] { "TargetId", "Position" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PatchDeploymentSteps");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_PatchDeploymentTargets_Id_ClientId",
                table: "PatchDeploymentTargets");

            migrationBuilder.DropColumn(
                name: "StepsReadAt",
                table: "PatchDeploymentTargets");
        }
    }
}
