using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NotificationRoutingAndWebhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllClients",
                table: "NotificationChannels",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedWebhook",
                table: "NotificationChannels",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebhookFormat",
                table: "NotificationChannels",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebhookHost",
                table: "NotificationChannels",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NotificationChannelClients",
                columns: table => new
                {
                    NotificationChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationChannelClients", x => new { x.NotificationChannelId, x.ClientId });
                    table.ForeignKey(
                        name: "FK_NotificationChannelClients_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NotificationChannelClients_NotificationChannels_Notificatio~",
                        column: x => x.NotificationChannelId,
                        principalTable: "NotificationChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OutboxWebhooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NotificationChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxWebhooks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutboxWebhooks_NotificationChannels_NotificationChannelId",
                        column: x => x.NotificationChannelId,
                        principalTable: "NotificationChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_NotificationChannels_Type",
                table: "NotificationChannels",
                sql: "(\"Type\" = 'Email' AND \"Recipients\" <> '' AND \"EncryptedWebhook\" IS NULL) OR (\"Type\" = 'Webhook' AND \"EncryptedWebhook\" IS NOT NULL AND \"WebhookFormat\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationChannelClients_ClientId",
                table: "NotificationChannelClients",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxWebhooks_NotificationChannelId_CreatedAt",
                table: "OutboxWebhooks",
                columns: new[] { "NotificationChannelId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxWebhooks_Pending",
                table: "OutboxWebhooks",
                column: "NextAttemptAt",
                filter: "\"SentAt\" IS NULL");

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_OutboxWebhooks_Notify" AFTER INSERT ON "OutboxWebhooks"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_notify_id('fleeto_outbox_webhooks');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS "TR_OutboxWebhooks_Notify" ON "OutboxWebhooks";""");

            migrationBuilder.DropTable(
                name: "NotificationChannelClients");

            migrationBuilder.DropTable(
                name: "OutboxWebhooks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_NotificationChannels_Type",
                table: "NotificationChannels");

            migrationBuilder.DropColumn(
                name: "AllClients",
                table: "NotificationChannels");

            migrationBuilder.DropColumn(
                name: "EncryptedWebhook",
                table: "NotificationChannels");

            migrationBuilder.DropColumn(
                name: "WebhookFormat",
                table: "NotificationChannels");

            migrationBuilder.DropColumn(
                name: "WebhookHost",
                table: "NotificationChannels");
        }
    }
}
