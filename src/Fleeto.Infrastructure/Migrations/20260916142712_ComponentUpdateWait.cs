using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ComponentUpdateWait : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "WaitAt",
                table: "EndpointComponentStates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WaitReason",
                table: "EndpointComponentStates",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WaitUntil",
                table: "EndpointComponentStates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WaitVersion",
                table: "EndpointComponentStates",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WaitAt",
                table: "EndpointComponentStates");

            migrationBuilder.DropColumn(
                name: "WaitReason",
                table: "EndpointComponentStates");

            migrationBuilder.DropColumn(
                name: "WaitUntil",
                table: "EndpointComponentStates");

            migrationBuilder.DropColumn(
                name: "WaitVersion",
                table: "EndpointComponentStates");
        }
    }
}
