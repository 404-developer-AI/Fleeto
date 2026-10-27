using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProxmoxWebInterface : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RemoteSessionClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SecretHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClaimedFrom = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ClaimedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Problem = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteSessionClaims", x => x.Id);
                    table.CheckConstraint("CK_RemoteSessionClaims_SecretHash", "octet_length(\"SecretHash\") = 32");
                    table.ForeignKey(
                        name: "FK_RemoteSessionClaims_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSessionClaims_CreatedAt",
                table: "RemoteSessionClaims",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSessionClaims_EndpointId_ClientId",
                table: "RemoteSessionClaims",
                columns: new[] { "EndpointId", "ClientId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RemoteSessionClaims");
        }
    }
}
