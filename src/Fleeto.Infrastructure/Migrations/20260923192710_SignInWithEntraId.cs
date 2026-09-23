using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SignInWithEntraId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EntraAccount",
                table: "AspNetUsers",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EntraLinkedAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EntraObjectId",
                table: "AspNetUsers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntraTenantId",
                table: "AspNetUsers",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SignInExchanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EncryptedRequest = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    RedirectUri = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EncryptedClaims = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignInExchanges", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_EntraObjectId",
                table: "AspNetUsers",
                column: "EntraObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SignInExchanges_State_CreatedAt",
                table: "SignInExchanges",
                columns: new[] { "State", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SignInExchanges");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_EntraObjectId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "EntraAccount",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "EntraLinkedAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "EntraObjectId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "EntraTenantId",
                table: "AspNetUsers");
        }
    }
}
