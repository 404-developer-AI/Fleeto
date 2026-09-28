using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MonitoringTemplateLevels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ForClients",
                table: "MonitoringTemplates",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ForEndpoints",
                table: "MonitoringTemplates",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ForSites",
                table: "MonitoringTemplates",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_MonitoringTemplates_Levels",
                table: "MonitoringTemplates",
                sql: "\"ForClients\" OR \"ForSites\" OR \"ForEndpoints\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MonitoringTemplates_Levels",
                table: "MonitoringTemplates");

            migrationBuilder.DropColumn(
                name: "ForClients",
                table: "MonitoringTemplates");

            migrationBuilder.DropColumn(
                name: "ForEndpoints",
                table: "MonitoringTemplates");

            migrationBuilder.DropColumn(
                name: "ForSites",
                table: "MonitoringTemplates");
        }
    }
}
