using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetify.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MaintenanceMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "MaintenanceEndsAt",
                table: "Sites",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MaintenanceReason",
                table: "Sites",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MaintenanceStartedAt",
                table: "Sites",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MaintenanceStartedByName",
                table: "Sites",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MaintenanceStartedByUserId",
                table: "Sites",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MaintenanceEndsAt",
                table: "Endpoints",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MaintenanceReason",
                table: "Endpoints",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MaintenanceStartedAt",
                table: "Endpoints",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MaintenanceStartedByName",
                table: "Endpoints",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MaintenanceStartedByUserId",
                table: "Endpoints",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MaintenanceEndsAt",
                table: "Clients",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MaintenanceReason",
                table: "Clients",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MaintenanceStartedAt",
                table: "Clients",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MaintenanceStartedByName",
                table: "Clients",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MaintenanceStartedByUserId",
                table: "Clients",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sites_MaintenanceEndsAt",
                table: "Sites",
                column: "MaintenanceEndsAt",
                filter: "\"MaintenanceEndsAt\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Sites_Maintenance",
                table: "Sites",
                sql: "\"MaintenanceEndsAt\" IS NULL OR \"MaintenanceStartedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Endpoints_MaintenanceEndsAt",
                table: "Endpoints",
                column: "MaintenanceEndsAt",
                filter: "\"MaintenanceEndsAt\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Endpoints_Maintenance",
                table: "Endpoints",
                sql: "\"MaintenanceEndsAt\" IS NULL OR \"MaintenanceStartedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_MaintenanceEndsAt",
                table: "Clients",
                column: "MaintenanceEndsAt",
                filter: "\"MaintenanceEndsAt\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Clients_Maintenance",
                table: "Clients",
                sql: "\"MaintenanceEndsAt\" IS NULL OR \"MaintenanceStartedAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sites_MaintenanceEndsAt",
                table: "Sites");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Sites_Maintenance",
                table: "Sites");

            migrationBuilder.DropIndex(
                name: "IX_Endpoints_MaintenanceEndsAt",
                table: "Endpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Endpoints_Maintenance",
                table: "Endpoints");

            migrationBuilder.DropIndex(
                name: "IX_Clients_MaintenanceEndsAt",
                table: "Clients");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Clients_Maintenance",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "MaintenanceEndsAt",
                table: "Sites");

            migrationBuilder.DropColumn(
                name: "MaintenanceReason",
                table: "Sites");

            migrationBuilder.DropColumn(
                name: "MaintenanceStartedAt",
                table: "Sites");

            migrationBuilder.DropColumn(
                name: "MaintenanceStartedByName",
                table: "Sites");

            migrationBuilder.DropColumn(
                name: "MaintenanceStartedByUserId",
                table: "Sites");

            migrationBuilder.DropColumn(
                name: "MaintenanceEndsAt",
                table: "Endpoints");

            migrationBuilder.DropColumn(
                name: "MaintenanceReason",
                table: "Endpoints");

            migrationBuilder.DropColumn(
                name: "MaintenanceStartedAt",
                table: "Endpoints");

            migrationBuilder.DropColumn(
                name: "MaintenanceStartedByName",
                table: "Endpoints");

            migrationBuilder.DropColumn(
                name: "MaintenanceStartedByUserId",
                table: "Endpoints");

            migrationBuilder.DropColumn(
                name: "MaintenanceEndsAt",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "MaintenanceReason",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "MaintenanceStartedAt",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "MaintenanceStartedByName",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "MaintenanceStartedByUserId",
                table: "Clients");
        }
    }
}
