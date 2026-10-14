using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <summary>
    /// Tasks linked to monitoring templates (0.6.0): a task runs on every endpoint that gets a template it is linked to. The
    /// client of a link is the client of the template or of the task, and a template and a task of two clients are never linked.
    /// </summary>
    public partial class TemplateTasks : Migration
    {
        internal const string ClientRuleSql = """
            CREATE OR REPLACE FUNCTION fleeto_template_task_client() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE template_client uuid;
            DECLARE task_client uuid;
            BEGIN
              SELECT "ClientId" INTO template_client FROM "MonitoringTemplates" WHERE "Id" = NEW."MonitoringTemplateId";
              SELECT "ClientId" INTO task_client FROM "AutomationTasks" WHERE "Id" = NEW."AutomationTaskId";
              IF template_client IS NOT NULL AND task_client IS NOT NULL AND template_client <> task_client THEN
                RAISE EXCEPTION 'A task and a monitoring template of two different clients cannot be linked' USING ERRCODE = 'integrity_constraint_violation';
              END IF;
              IF NEW."ClientId" IS DISTINCT FROM COALESCE(template_client, task_client) THEN
                RAISE EXCEPTION 'The client of a link between a task and a monitoring template is the client of either' USING ERRCODE = 'integrity_constraint_violation';
              END IF;
              RETURN NEW;
            END $$;
            CREATE CONSTRAINT TRIGGER "TR_MonitoringTemplateAutomationTasks_Client" AFTER INSERT OR UPDATE ON "MonitoringTemplateAutomationTasks"
              FOR EACH ROW EXECUTE FUNCTION fleeto_template_task_client();
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MonitoringTemplateAutomationTasks",
                columns: table => new
                {
                    MonitoringTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringTemplateAutomationTasks", x => new { x.MonitoringTemplateId, x.AutomationTaskId });
                    table.ForeignKey(
                        name: "FK_MonitoringTemplateAutomationTasks_AutomationTasks_Automatio~",
                        column: x => x.AutomationTaskId,
                        principalTable: "AutomationTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MonitoringTemplateAutomationTasks_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MonitoringTemplateAutomationTasks_MonitoringTemplates_Monit~",
                        column: x => x.MonitoringTemplateId,
                        principalTable: "MonitoringTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringTemplateAutomationTasks_AutomationTaskId",
                table: "MonitoringTemplateAutomationTasks",
                column: "AutomationTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringTemplateAutomationTasks_ClientId",
                table: "MonitoringTemplateAutomationTasks",
                column: "ClientId");

            migrationBuilder.Sql(ClientRuleSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MonitoringTemplateAutomationTasks");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS fleeto_template_task_client();");
        }
    }
}
