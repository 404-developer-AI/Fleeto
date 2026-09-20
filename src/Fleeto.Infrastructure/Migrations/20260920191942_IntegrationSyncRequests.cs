using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <summary>
    /// What web asks the workers to do with an integration, and what they read back (0.4.0 step 1), additive. Web has no
    /// outbound access, so a connection test is a request on the row (<c>SyncRequestedAt</c>) that the workers run; the
    /// tenants they read are stored with the time they were read, and web shows those.
    /// </summary>
    public partial class IntegrationSyncRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SyncRequestedAt",
                table: "Integrations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TenantsJson",
                table: "Integrations",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<DateTime>(
                name: "TenantsUpdatedAt",
                table: "Integrations",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SyncRequestedAt",
                table: "Integrations");

            migrationBuilder.DropColumn(
                name: "TenantsJson",
                table: "Integrations");

            migrationBuilder.DropColumn(
                name: "TenantsUpdatedAt",
                table: "Integrations");
        }
    }
}
