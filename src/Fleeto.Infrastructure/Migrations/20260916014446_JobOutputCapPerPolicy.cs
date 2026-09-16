using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class JobOutputCapPerPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MaxOutputBytes",
                table: "Policies",
                type: "bigint",
                nullable: false,
                defaultValue: 52428800L);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Policies_MaxOutputBytes",
                table: "Policies",
                sql: "\"MaxOutputBytes\" BETWEEN 1048576 AND 209715200");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Policies_MaxOutputBytes",
                table: "Policies");

            migrationBuilder.DropColumn(
                name: "MaxOutputBytes",
                table: "Policies");
        }
    }
}
