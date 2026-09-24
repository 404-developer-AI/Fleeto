using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NotificationDigests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BundledInto",
                table: "OutboxWebhooks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DigestLine",
                table: "OutboxWebhooks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BundledInto",
                table: "OutboxEmails",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DigestLine",
                table: "OutboxEmails",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEmails_ToAddress_CreatedAt",
                table: "OutboxEmails",
                columns: new[] { "ToAddress", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxEmails_ToAddress_CreatedAt",
                table: "OutboxEmails");

            migrationBuilder.DropColumn(
                name: "BundledInto",
                table: "OutboxWebhooks");

            migrationBuilder.DropColumn(
                name: "DigestLine",
                table: "OutboxWebhooks");

            migrationBuilder.DropColumn(
                name: "BundledInto",
                table: "OutboxEmails");

            migrationBuilder.DropColumn(
                name: "DigestLine",
                table: "OutboxEmails");
        }
    }
}
