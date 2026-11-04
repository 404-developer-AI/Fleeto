using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PolicyConsentTexts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RemoteConsentAllowLabel",
                table: "Policies",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteConsentDenyLabel",
                table: "Policies",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteConsentMessage",
                table: "Policies",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteConsentTitle",
                table: "Policies",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RemoteConsentAllowLabel",
                table: "Policies");

            migrationBuilder.DropColumn(
                name: "RemoteConsentDenyLabel",
                table: "Policies");

            migrationBuilder.DropColumn(
                name: "RemoteConsentMessage",
                table: "Policies");

            migrationBuilder.DropColumn(
                name: "RemoteConsentTitle",
                table: "Policies");
        }
    }
}
