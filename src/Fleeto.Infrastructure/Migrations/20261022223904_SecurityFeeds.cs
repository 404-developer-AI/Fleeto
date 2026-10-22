using System;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SecurityFeeds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IpFeeds",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EntryCount = table.Column<int>(type: "integer", nullable: false),
                    Problem = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    RefreshRequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpFeeds", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "IpFeedEntries",
                columns: table => new
                {
                    Feed = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Network = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpFeedEntries", x => new { x.Feed, x.Network });
                    table.ForeignKey(
                        name: "FK_IpFeedEntries_IpFeeds_Feed",
                        column: x => x.Feed,
                        principalTable: "IpFeeds",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(SecuritySql.FeedsUp);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SecuritySql.FeedsDown);

            migrationBuilder.DropTable(
                name: "IpFeedEntries");

            migrationBuilder.DropTable(
                name: "IpFeeds");
        }
    }
}
