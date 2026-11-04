using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WindowsPatchScans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "PatchScans",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourceServer",
                table: "PatchScans",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ScanSource",
                table: "EndpointPatchStates",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ScanSourceServer",
                table: "EndpointPatchStates",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "Downloaded",
                table: "EndpointMissingUpdates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDriver",
                table: "EndpointMissingUpdates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsFeatureUpdate",
                table: "EndpointMissingUpdates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "SizeBytes",
                table: "EndpointMissingUpdates",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Source",
                table: "PatchScans");

            migrationBuilder.DropColumn(
                name: "SourceServer",
                table: "PatchScans");

            migrationBuilder.DropColumn(
                name: "ScanSource",
                table: "EndpointPatchStates");

            migrationBuilder.DropColumn(
                name: "ScanSourceServer",
                table: "EndpointPatchStates");

            migrationBuilder.DropColumn(
                name: "Downloaded",
                table: "EndpointMissingUpdates");

            migrationBuilder.DropColumn(
                name: "IsDriver",
                table: "EndpointMissingUpdates");

            migrationBuilder.DropColumn(
                name: "IsFeatureUpdate",
                table: "EndpointMissingUpdates");

            migrationBuilder.DropColumn(
                name: "SizeBytes",
                table: "EndpointMissingUpdates");
        }
    }
}
