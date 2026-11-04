using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PatchPolicyEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Engine",
                table: "PatchPolicies",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Action1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PatchPolicies_Engine",
                table: "PatchPolicies",
                sql: "\"Engine\" IN ('Action1', 'Fleeto')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PatchPolicies_Engine",
                table: "PatchPolicies");

            migrationBuilder.DropColumn(
                name: "Engine",
                table: "PatchPolicies");
        }
    }
}
