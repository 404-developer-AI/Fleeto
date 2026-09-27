using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MissingUpdateDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovalStatus",
                table: "EndpointMissingUpdates",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<List<string>>(
                name: "Cves",
                table: "EndpointMissingUpdates",
                type: "text[]",
                nullable: false,
                // A default, so the release before this one can still insert rows without the column (expand/contract).
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string>(
                name: "InstalledVersion",
                table: "EndpointMissingUpdates",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateOnly>(
                name: "ReleaseDate",
                table: "EndpointMissingUpdates",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdateType",
                table: "EndpointMissingUpdates",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovalStatus",
                table: "EndpointMissingUpdates");

            migrationBuilder.DropColumn(
                name: "Cves",
                table: "EndpointMissingUpdates");

            migrationBuilder.DropColumn(
                name: "InstalledVersion",
                table: "EndpointMissingUpdates");

            migrationBuilder.DropColumn(
                name: "ReleaseDate",
                table: "EndpointMissingUpdates");

            migrationBuilder.DropColumn(
                name: "UpdateType",
                table: "EndpointMissingUpdates");
        }
    }
}
