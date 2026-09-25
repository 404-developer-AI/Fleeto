using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PatchPoliciesAndLinksOnEveryLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OtherAutomationsJson",
                table: "IntegrationMappings",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<DateTime>(
                name: "OtherAutomationsReadAt",
                table: "IntegrationMappings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PatchPolicyId",
                table: "ClientTemplateSites",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PatchPolicyId",
                table: "ClientTemplates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PolicyId",
                table: "ClientTemplates",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClientMonitoringTemplates",
                columns: table => new
                {
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    MonitoringTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientMonitoringTemplates", x => new { x.ClientId, x.MonitoringTemplateId });
                    table.ForeignKey(
                        name: "FK_ClientMonitoringTemplates_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientMonitoringTemplates_MonitoringTemplates_MonitoringTem~",
                        column: x => x.MonitoringTemplateId,
                        principalTable: "MonitoringTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientPolicies",
                columns: table => new
                {
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientPolicies", x => x.ClientId);
                    table.ForeignKey(
                        name: "FK_ClientPolicies_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientPolicies_Policies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "Policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientTemplateMonitoringTemplates",
                columns: table => new
                {
                    ClientTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    MonitoringTemplateId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientTemplateMonitoringTemplates", x => new { x.ClientTemplateId, x.MonitoringTemplateId });
                    table.ForeignKey(
                        name: "FK_ClientTemplateMonitoringTemplates_ClientTemplates_ClientTem~",
                        column: x => x.ClientTemplateId,
                        principalTable: "ClientTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientTemplateMonitoringTemplates_MonitoringTemplates_Monit~",
                        column: x => x.MonitoringTemplateId,
                        principalTable: "MonitoringTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EndpointPolicies",
                columns: table => new
                {
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndpointPolicies", x => x.EndpointId);
                    table.ForeignKey(
                        name: "FK_EndpointPolicies_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EndpointPolicies_Policies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "Policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationAutomations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalTenantId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PatchPolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalAutomationId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SyncedHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationAutomations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IntegrationAutomations_Integrations_IntegrationId",
                        column: x => x.IntegrationId,
                        principalTable: "Integrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatchPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ScheduleKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WeekDays = table.Column<int>(type: "integer", nullable: false),
                    MonthDay = table.Column<int>(type: "integer", nullable: false),
                    MonthWeek = table.Column<int>(type: "integer", nullable: false),
                    MonthWeekday = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    StartMinute = table.Column<int>(type: "integer", nullable: false),
                    EndpointLocalTime = table.Column<bool>(type: "boolean", nullable: false),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UpdateSources = table.Column<List<string>>(type: "text[]", nullable: false),
                    UpdateTypes = table.Column<List<string>>(type: "text[]", nullable: false),
                    Severities = table.Column<List<string>>(type: "text[]", nullable: false),
                    ExcludedNames = table.Column<List<string>>(type: "text[]", nullable: false),
                    ExcludedVendors = table.Column<List<string>>(type: "text[]", nullable: false),
                    RequireApproval = table.Column<bool>(type: "boolean", nullable: false),
                    InstallDelayDays = table.Column<int>(type: "integer", nullable: false),
                    AutoReboot = table.Column<bool>(type: "boolean", nullable: false),
                    RebootMessage = table.Column<bool>(type: "boolean", nullable: false),
                    RebootMessageText = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RebootTimeoutMinutes = table.Column<int>(type: "integer", nullable: false),
                    RetryHours = table.Column<int>(type: "integer", nullable: false),
                    CopiedFromId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatchPolicies", x => x.Id);
                    table.CheckConstraint("CK_PatchPolicies_InstallDelayDays", "\"InstallDelayDays\" BETWEEN 0 AND 90");
                    table.CheckConstraint("CK_PatchPolicies_MonthDay", "\"MonthDay\" BETWEEN 1 AND 31");
                    table.CheckConstraint("CK_PatchPolicies_MonthWeek", "\"MonthWeek\" BETWEEN 1 AND 4");
                    table.CheckConstraint("CK_PatchPolicies_RebootTimeoutMinutes", "\"RebootTimeoutMinutes\" BETWEEN 1 AND 1440");
                    table.CheckConstraint("CK_PatchPolicies_RetryHours", "\"RetryHours\" BETWEEN 1 AND 168");
                    table.CheckConstraint("CK_PatchPolicies_StartMinute", "\"StartMinute\" BETWEEN 0 AND 1439");
                    table.CheckConstraint("CK_PatchPolicies_WeekDays", "\"WeekDays\" BETWEEN 0 AND 127");
                    table.ForeignKey(
                        name: "FK_PatchPolicies_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientPatchPolicies",
                columns: table => new
                {
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    PatchPolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientPatchPolicies", x => x.ClientId);
                    table.ForeignKey(
                        name: "FK_ClientPatchPolicies_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientPatchPolicies_PatchPolicies_PatchPolicyId",
                        column: x => x.PatchPolicyId,
                        principalTable: "PatchPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EndpointPatchPolicies",
                columns: table => new
                {
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    PatchPolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndpointPatchPolicies", x => x.EndpointId);
                    table.ForeignKey(
                        name: "FK_EndpointPatchPolicies_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EndpointPatchPolicies_PatchPolicies_PatchPolicyId",
                        column: x => x.PatchPolicyId,
                        principalTable: "PatchPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SitePatchPolicies",
                columns: table => new
                {
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    PatchPolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SitePatchPolicies", x => x.SiteId);
                    table.ForeignKey(
                        name: "FK_SitePatchPolicies_PatchPolicies_PatchPolicyId",
                        column: x => x.PatchPolicyId,
                        principalTable: "PatchPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SitePatchPolicies_Sites_SiteId_ClientId",
                        columns: x => new { x.SiteId, x.ClientId },
                        principalTable: "Sites",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientTemplateSites_PatchPolicyId",
                table: "ClientTemplateSites",
                column: "PatchPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientTemplates_PatchPolicyId",
                table: "ClientTemplates",
                column: "PatchPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientTemplates_PolicyId",
                table: "ClientTemplates",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientMonitoringTemplates_MonitoringTemplateId",
                table: "ClientMonitoringTemplates",
                column: "MonitoringTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientPatchPolicies_PatchPolicyId",
                table: "ClientPatchPolicies",
                column: "PatchPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientPolicies_PolicyId",
                table: "ClientPolicies",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientTemplateMonitoringTemplates_MonitoringTemplateId",
                table: "ClientTemplateMonitoringTemplates",
                column: "MonitoringTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_EndpointPatchPolicies_EndpointId_ClientId",
                table: "EndpointPatchPolicies",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_EndpointPatchPolicies_PatchPolicyId",
                table: "EndpointPatchPolicies",
                column: "PatchPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_EndpointPolicies_EndpointId_ClientId",
                table: "EndpointPolicies",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_EndpointPolicies_PolicyId",
                table: "EndpointPolicies",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationAutomations_IntegrationId_ClientId_PatchPolicyId",
                table: "IntegrationAutomations",
                columns: new[] { "IntegrationId", "ClientId", "PatchPolicyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationAutomations_PatchPolicyId",
                table: "IntegrationAutomations",
                column: "PatchPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_PatchPolicies_ClientId_Name",
                table: "PatchPolicies",
                columns: new[] { "ClientId", "Name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_SitePatchPolicies_PatchPolicyId",
                table: "SitePatchPolicies",
                column: "PatchPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_SitePatchPolicies_SiteId_ClientId",
                table: "SitePatchPolicies",
                columns: new[] { "SiteId", "ClientId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientTemplates_PatchPolicies_PatchPolicyId",
                table: "ClientTemplates",
                column: "PatchPolicyId",
                principalTable: "PatchPolicies",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientTemplates_Policies_PolicyId",
                table: "ClientTemplates",
                column: "PolicyId",
                principalTable: "Policies",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientTemplateSites_PatchPolicies_PatchPolicyId",
                table: "ClientTemplateSites",
                column: "PatchPolicyId",
                principalTable: "PatchPolicies",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // The same rules as for the links of a site (DatabaseRulesSql): a link never reaches a policy, patch policy or
            // monitoring template of another client, a client template only uses global ones, and a patch policy stays
            // with the client it was made for.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION fleeto_link_same_client() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE owner uuid;
                BEGIN
                  EXECUTE format('SELECT "ClientId" FROM %I WHERE "Id" = $1', TG_ARGV[0]) INTO owner
                    USING (to_jsonb(NEW) ->> TG_ARGV[1])::uuid;
                  IF owner IS NOT NULL AND owner <> NEW."ClientId" THEN
                    RAISE EXCEPTION 'A link cannot use a % of another client', TG_ARGV[2] USING ERRCODE = 'integrity_constraint_violation';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE CONSTRAINT TRIGGER "TR_ClientPolicies_Client" AFTER INSERT OR UPDATE ON "ClientPolicies"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_link_same_client('Policies', 'PolicyId', 'policy');
                CREATE CONSTRAINT TRIGGER "TR_EndpointPolicies_Client" AFTER INSERT OR UPDATE ON "EndpointPolicies"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_link_same_client('Policies', 'PolicyId', 'policy');
                CREATE CONSTRAINT TRIGGER "TR_ClientMonitoringTemplates_Client" AFTER INSERT OR UPDATE ON "ClientMonitoringTemplates"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_link_same_client('MonitoringTemplates', 'MonitoringTemplateId', 'monitoring template');
                CREATE CONSTRAINT TRIGGER "TR_ClientPatchPolicies_Client" AFTER INSERT OR UPDATE ON "ClientPatchPolicies"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_link_same_client('PatchPolicies', 'PatchPolicyId', 'patch policy');
                CREATE CONSTRAINT TRIGGER "TR_SitePatchPolicies_Client" AFTER INSERT OR UPDATE ON "SitePatchPolicies"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_link_same_client('PatchPolicies', 'PatchPolicyId', 'patch policy');
                CREATE CONSTRAINT TRIGGER "TR_EndpointPatchPolicies_Client" AFTER INSERT OR UPDATE ON "EndpointPatchPolicies"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_link_same_client('PatchPolicies', 'PatchPolicyId', 'patch policy');

                CREATE OR REPLACE FUNCTION fleeto_template_uses_global() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE ref uuid; owner uuid;
                BEGIN
                  ref := (to_jsonb(NEW) ->> TG_ARGV[1])::uuid;
                  IF ref IS NOT NULL THEN
                    EXECUTE format('SELECT "ClientId" FROM %I WHERE "Id" = $1', TG_ARGV[0]) INTO owner USING ref;
                    IF owner IS NOT NULL THEN
                      RAISE EXCEPTION 'A client template can only use global %', TG_ARGV[2] USING ERRCODE = 'integrity_constraint_violation';
                    END IF;
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE CONSTRAINT TRIGGER "TR_ClientTemplates_GlobalPolicy" AFTER INSERT OR UPDATE ON "ClientTemplates"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_template_uses_global('Policies', 'PolicyId', 'policies');
                CREATE CONSTRAINT TRIGGER "TR_ClientTemplates_GlobalPatchPolicy" AFTER INSERT OR UPDATE ON "ClientTemplates"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_template_uses_global('PatchPolicies', 'PatchPolicyId', 'patch policies');
                CREATE CONSTRAINT TRIGGER "TR_ClientTemplateSites_GlobalPatchPolicy" AFTER INSERT OR UPDATE ON "ClientTemplateSites"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_template_uses_global('PatchPolicies', 'PatchPolicyId', 'patch policies');
                CREATE CONSTRAINT TRIGGER "TR_ClientTemplateMonitoringTemplates_Global" AFTER INSERT OR UPDATE ON "ClientTemplateMonitoringTemplates"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_template_uses_global('MonitoringTemplates', 'MonitoringTemplateId', 'monitoring templates');

                CREATE TRIGGER "TR_PatchPolicies_ClientIdImmutable" BEFORE UPDATE OF "ClientId" ON "PatchPolicies"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_client_id_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_PatchPolicies_ClientIdImmutable" ON "PatchPolicies";
                DROP TRIGGER IF EXISTS "TR_ClientTemplateMonitoringTemplates_Global" ON "ClientTemplateMonitoringTemplates";
                DROP TRIGGER IF EXISTS "TR_ClientTemplateSites_GlobalPatchPolicy" ON "ClientTemplateSites";
                DROP TRIGGER IF EXISTS "TR_ClientTemplates_GlobalPatchPolicy" ON "ClientTemplates";
                DROP TRIGGER IF EXISTS "TR_ClientTemplates_GlobalPolicy" ON "ClientTemplates";
                DROP FUNCTION IF EXISTS fleeto_template_uses_global();
                DROP TRIGGER IF EXISTS "TR_EndpointPatchPolicies_Client" ON "EndpointPatchPolicies";
                DROP TRIGGER IF EXISTS "TR_SitePatchPolicies_Client" ON "SitePatchPolicies";
                DROP TRIGGER IF EXISTS "TR_ClientPatchPolicies_Client" ON "ClientPatchPolicies";
                DROP TRIGGER IF EXISTS "TR_ClientMonitoringTemplates_Client" ON "ClientMonitoringTemplates";
                DROP TRIGGER IF EXISTS "TR_EndpointPolicies_Client" ON "EndpointPolicies";
                DROP TRIGGER IF EXISTS "TR_ClientPolicies_Client" ON "ClientPolicies";
                DROP FUNCTION IF EXISTS fleeto_link_same_client();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_ClientTemplates_PatchPolicies_PatchPolicyId",
                table: "ClientTemplates");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientTemplates_Policies_PolicyId",
                table: "ClientTemplates");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientTemplateSites_PatchPolicies_PatchPolicyId",
                table: "ClientTemplateSites");

            migrationBuilder.DropTable(
                name: "ClientMonitoringTemplates");

            migrationBuilder.DropTable(
                name: "ClientPatchPolicies");

            migrationBuilder.DropTable(
                name: "ClientPolicies");

            migrationBuilder.DropTable(
                name: "ClientTemplateMonitoringTemplates");

            migrationBuilder.DropTable(
                name: "EndpointPatchPolicies");

            migrationBuilder.DropTable(
                name: "EndpointPolicies");

            migrationBuilder.DropTable(
                name: "IntegrationAutomations");

            migrationBuilder.DropTable(
                name: "SitePatchPolicies");

            migrationBuilder.DropTable(
                name: "PatchPolicies");

            migrationBuilder.DropIndex(
                name: "IX_ClientTemplateSites_PatchPolicyId",
                table: "ClientTemplateSites");

            migrationBuilder.DropIndex(
                name: "IX_ClientTemplates_PatchPolicyId",
                table: "ClientTemplates");

            migrationBuilder.DropIndex(
                name: "IX_ClientTemplates_PolicyId",
                table: "ClientTemplates");

            migrationBuilder.DropColumn(
                name: "OtherAutomationsJson",
                table: "IntegrationMappings");

            migrationBuilder.DropColumn(
                name: "OtherAutomationsReadAt",
                table: "IntegrationMappings");

            migrationBuilder.DropColumn(
                name: "PatchPolicyId",
                table: "ClientTemplateSites");

            migrationBuilder.DropColumn(
                name: "PatchPolicyId",
                table: "ClientTemplates");

            migrationBuilder.DropColumn(
                name: "PolicyId",
                table: "ClientTemplates");
        }
    }
}
