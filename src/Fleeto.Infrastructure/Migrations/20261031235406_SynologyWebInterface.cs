using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SynologyWebInterface : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "RemoteSessionClaims",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "ProxmoxWeb");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RemoteSessionClaims_Kind",
                table: "RemoteSessionClaims",
                sql: "\"Kind\" IN ('ProxmoxWeb', 'SynologyWeb')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RemoteSessionClaims_Kind",
                table: "RemoteSessionClaims");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "RemoteSessionClaims");
        }
    }
}
