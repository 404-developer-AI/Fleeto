using System;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SecurityMonitor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IpAllows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Network = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpAllows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IpBlocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Network = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpBlocks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SecuritySignals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Minute = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Component = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Network = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Subject = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecuritySignals", x => new { x.Minute, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "UserSignInNetworks",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Network = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FirstSeen = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeen = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSignInNetworks", x => new { x.UserId, x.Network });
                    table.ForeignKey(
                        name: "FK_UserSignInNetworks_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IpAllows_Network",
                table: "IpAllows",
                column: "Network",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IpBlocks_ExpiresAt",
                table: "IpBlocks",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_IpBlocks_Network",
                table: "IpBlocks",
                column: "Network",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecuritySignals_Kind_Minute",
                table: "SecuritySignals",
                columns: new[] { "Kind", "Minute" });

            migrationBuilder.CreateIndex(
                name: "IX_SecuritySignals_Network_Minute",
                table: "SecuritySignals",
                columns: new[] { "Network", "Minute" });

            migrationBuilder.CreateIndex(
                name: "IX_UserSignInNetworks_Network_LastSeen",
                table: "UserSignInNetworks",
                columns: new[] { "Network", "LastSeen" });

            migrationBuilder.Sql(SecuritySql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SecuritySql.Down);

            migrationBuilder.DropTable(
                name: "IpAllows");

            migrationBuilder.DropTable(
                name: "IpBlocks");

            migrationBuilder.DropTable(
                name: "SecuritySignals");

            migrationBuilder.DropTable(
                name: "UserSignInNetworks");
        }
    }
}
