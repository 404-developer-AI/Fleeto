using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoteControlWindowsSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WindowsSessionId",
                table: "RemoteSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RemoteSessions_WindowsSession",
                table: "RemoteSessions",
                sql: "(\"Kind\" = 'RemoteControl' AND \"WindowsSessionId\" >= 0) OR (\"Kind\" <> 'RemoteControl' AND \"WindowsSessionId\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RemoteSessions_WindowsSession",
                table: "RemoteSessions");

            migrationBuilder.DropColumn(
                name: "WindowsSessionId",
                table: "RemoteSessions");
        }
    }
}
