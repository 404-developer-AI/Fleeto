using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ChosenSignedInUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RunAsChosenAccount",
                table: "Jobs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RunAsUserId",
                table: "Jobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignedInUsersAt",
                table: "Endpoints",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignedInUsersJson",
                table: "Endpoints",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RunAsChosenAccount",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "RunAsUserId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "SignedInUsersAt",
                table: "Endpoints");

            migrationBuilder.DropColumn(
                name: "SignedInUsersJson",
                table: "Endpoints");
        }
    }
}
