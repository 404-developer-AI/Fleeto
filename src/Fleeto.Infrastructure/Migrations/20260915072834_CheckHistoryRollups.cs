using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CheckHistoryRollups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CheckResultsDaily",
                columns: table => new
                {
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Target = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Bucket = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    MinValue = table.Column<double>(type: "double precision", nullable: true),
                    MaxValue = table.Column<double>(type: "double precision", nullable: true),
                    SumValue = table.Column<double>(type: "double precision", nullable: false),
                    ValueCount = table.Column<int>(type: "integer", nullable: false),
                    ErrorCount = table.Column<int>(type: "integer", nullable: false),
                    NoResponseCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckResultsDaily", x => new { x.EndpointId, x.CheckDefinitionId, x.Target, x.Bucket });
                    table.ForeignKey(
                        name: "FK_CheckResultsDaily_CheckDefinitions_CheckDefinitionId",
                        column: x => x.CheckDefinitionId,
                        principalTable: "CheckDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CheckResultsDaily_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CheckResultsHourly",
                columns: table => new
                {
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Target = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Bucket = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    MinValue = table.Column<double>(type: "double precision", nullable: true),
                    MaxValue = table.Column<double>(type: "double precision", nullable: true),
                    SumValue = table.Column<double>(type: "double precision", nullable: false),
                    ValueCount = table.Column<int>(type: "integer", nullable: false),
                    ErrorCount = table.Column<int>(type: "integer", nullable: false),
                    NoResponseCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckResultsHourly", x => new { x.EndpointId, x.CheckDefinitionId, x.Target, x.Bucket });
                    table.ForeignKey(
                        name: "FK_CheckResultsHourly_CheckDefinitions_CheckDefinitionId",
                        column: x => x.CheckDefinitionId,
                        principalTable: "CheckDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CheckResultsHourly_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CheckResultsDaily_Bucket",
                table: "CheckResultsDaily",
                column: "Bucket");

            migrationBuilder.CreateIndex(
                name: "IX_CheckResultsDaily_CheckDefinitionId",
                table: "CheckResultsDaily",
                column: "CheckDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_CheckResultsDaily_EndpointId_ClientId",
                table: "CheckResultsDaily",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_CheckResultsHourly_Bucket",
                table: "CheckResultsHourly",
                column: "Bucket");

            migrationBuilder.CreateIndex(
                name: "IX_CheckResultsHourly_CheckDefinitionId",
                table: "CheckResultsHourly",
                column: "CheckDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_CheckResultsHourly_EndpointId_ClientId",
                table: "CheckResultsHourly",
                columns: new[] { "EndpointId", "ClientId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CheckResultsDaily");

            migrationBuilder.DropTable(
                name: "CheckResultsHourly");
        }
    }
}
