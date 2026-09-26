using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PoliciesPerEndpointClass : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_SitePolicies",
                table: "SitePolicies");

            migrationBuilder.DropIndex(
                name: "IX_SitePolicies_SiteId_ClientId",
                table: "SitePolicies");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SitePatchPolicies",
                table: "SitePatchPolicies");

            migrationBuilder.DropIndex(
                name: "IX_SitePatchPolicies_SiteId_ClientId",
                table: "SitePatchPolicies");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ClientPolicies",
                table: "ClientPolicies");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ClientPatchPolicies",
                table: "ClientPatchPolicies");

            migrationBuilder.AddColumn<string>(
                name: "AppliesTo",
                table: "SitePolicies",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "All");

            migrationBuilder.AddColumn<string>(
                name: "AppliesTo",
                table: "SitePatchPolicies",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "All");

            migrationBuilder.AddColumn<string>(
                name: "AppliesTo",
                table: "Policies",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "All");

            migrationBuilder.AddColumn<string>(
                name: "AppliesTo",
                table: "PatchPolicies",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "All");

            migrationBuilder.AddColumn<string>(
                name: "AppliesTo",
                table: "MonitoringTemplates",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "All");

            migrationBuilder.AddColumn<Guid>(
                name: "ServerPatchPolicyId",
                table: "ClientTemplateSites",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ServerPolicyId",
                table: "ClientTemplateSites",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkstationPatchPolicyId",
                table: "ClientTemplateSites",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkstationPolicyId",
                table: "ClientTemplateSites",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ServerPatchPolicyId",
                table: "ClientTemplates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ServerPolicyId",
                table: "ClientTemplates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkstationPatchPolicyId",
                table: "ClientTemplates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkstationPolicyId",
                table: "ClientTemplates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppliesTo",
                table: "ClientPolicies",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "All");

            migrationBuilder.AddColumn<string>(
                name: "AppliesTo",
                table: "ClientPatchPolicies",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "All");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SitePolicies",
                table: "SitePolicies",
                columns: new[] { "SiteId", "AppliesTo" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_SitePatchPolicies",
                table: "SitePatchPolicies",
                columns: new[] { "SiteId", "AppliesTo" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_ClientPolicies",
                table: "ClientPolicies",
                columns: new[] { "ClientId", "AppliesTo" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_ClientPatchPolicies",
                table: "ClientPatchPolicies",
                columns: new[] { "ClientId", "AppliesTo" });

            migrationBuilder.CreateIndex(
                name: "IX_SitePolicies_SiteId_ClientId",
                table: "SitePolicies",
                columns: new[] { "SiteId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_SitePatchPolicies_SiteId_ClientId",
                table: "SitePatchPolicies",
                columns: new[] { "SiteId", "ClientId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Policies_DefaultForAll",
                table: "Policies",
                sql: "NOT \"IsDefault\" OR \"AppliesTo\" = 'All'");

            migrationBuilder.CreateIndex(
                name: "IX_ClientTemplateSites_ServerPatchPolicyId",
                table: "ClientTemplateSites",
                column: "ServerPatchPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientTemplateSites_ServerPolicyId",
                table: "ClientTemplateSites",
                column: "ServerPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientTemplateSites_WorkstationPatchPolicyId",
                table: "ClientTemplateSites",
                column: "WorkstationPatchPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientTemplateSites_WorkstationPolicyId",
                table: "ClientTemplateSites",
                column: "WorkstationPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientTemplates_ServerPatchPolicyId",
                table: "ClientTemplates",
                column: "ServerPatchPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientTemplates_ServerPolicyId",
                table: "ClientTemplates",
                column: "ServerPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientTemplates_WorkstationPatchPolicyId",
                table: "ClientTemplates",
                column: "WorkstationPatchPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientTemplates_WorkstationPolicyId",
                table: "ClientTemplates",
                column: "WorkstationPolicyId");

            migrationBuilder.AddForeignKey(
                name: "FK_ClientTemplates_PatchPolicies_ServerPatchPolicyId",
                table: "ClientTemplates",
                column: "ServerPatchPolicyId",
                principalTable: "PatchPolicies",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientTemplates_PatchPolicies_WorkstationPatchPolicyId",
                table: "ClientTemplates",
                column: "WorkstationPatchPolicyId",
                principalTable: "PatchPolicies",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientTemplates_Policies_ServerPolicyId",
                table: "ClientTemplates",
                column: "ServerPolicyId",
                principalTable: "Policies",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientTemplates_Policies_WorkstationPolicyId",
                table: "ClientTemplates",
                column: "WorkstationPolicyId",
                principalTable: "Policies",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientTemplateSites_PatchPolicies_ServerPatchPolicyId",
                table: "ClientTemplateSites",
                column: "ServerPatchPolicyId",
                principalTable: "PatchPolicies",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientTemplateSites_PatchPolicies_WorkstationPatchPolicyId",
                table: "ClientTemplateSites",
                column: "WorkstationPatchPolicyId",
                principalTable: "PatchPolicies",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientTemplateSites_Policies_ServerPolicyId",
                table: "ClientTemplateSites",
                column: "ServerPolicyId",
                principalTable: "Policies",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientTemplateSites_Policies_WorkstationPolicyId",
                table: "ClientTemplateSites",
                column: "WorkstationPolicyId",
                principalTable: "Policies",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // The choices per class of a client template are global only, like the choice for every endpoint.
            migrationBuilder.Sql("""
                CREATE CONSTRAINT TRIGGER "TR_ClientTemplates_GlobalServerPolicyId" AFTER INSERT OR UPDATE ON "ClientTemplates"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_template_uses_global('Policies', 'ServerPolicyId', 'policies');
                CREATE CONSTRAINT TRIGGER "TR_ClientTemplates_GlobalWorkstationPolicyId" AFTER INSERT OR UPDATE ON "ClientTemplates"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_template_uses_global('Policies', 'WorkstationPolicyId', 'policies');
                CREATE CONSTRAINT TRIGGER "TR_ClientTemplates_GlobalServerPatchPolicyId" AFTER INSERT OR UPDATE ON "ClientTemplates"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_template_uses_global('PatchPolicies', 'ServerPatchPolicyId', 'patch policies');
                CREATE CONSTRAINT TRIGGER "TR_ClientTemplates_GlobalWorkstationPatchPolicyId" AFTER INSERT OR UPDATE ON "ClientTemplates"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_template_uses_global('PatchPolicies', 'WorkstationPatchPolicyId', 'patch policies');
                CREATE CONSTRAINT TRIGGER "TR_ClientTemplateSites_GlobalServerPolicyId" AFTER INSERT OR UPDATE ON "ClientTemplateSites"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_template_uses_global('Policies', 'ServerPolicyId', 'policies');
                CREATE CONSTRAINT TRIGGER "TR_ClientTemplateSites_GlobalWorkstationPolicyId" AFTER INSERT OR UPDATE ON "ClientTemplateSites"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_template_uses_global('Policies', 'WorkstationPolicyId', 'policies');
                CREATE CONSTRAINT TRIGGER "TR_ClientTemplateSites_GlobalServerPatchPolicyId" AFTER INSERT OR UPDATE ON "ClientTemplateSites"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_template_uses_global('PatchPolicies', 'ServerPatchPolicyId', 'patch policies');
                CREATE CONSTRAINT TRIGGER "TR_ClientTemplateSites_GlobalWorkstationPatchPolicyId" AFTER INSERT OR UPDATE ON "ClientTemplateSites"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_template_uses_global('PatchPolicies', 'WorkstationPatchPolicyId', 'patch policies');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_ClientTemplates_GlobalServerPolicyId" ON "ClientTemplates";
                DROP TRIGGER IF EXISTS "TR_ClientTemplates_GlobalWorkstationPolicyId" ON "ClientTemplates";
                DROP TRIGGER IF EXISTS "TR_ClientTemplates_GlobalServerPatchPolicyId" ON "ClientTemplates";
                DROP TRIGGER IF EXISTS "TR_ClientTemplates_GlobalWorkstationPatchPolicyId" ON "ClientTemplates";
                DROP TRIGGER IF EXISTS "TR_ClientTemplateSites_GlobalServerPolicyId" ON "ClientTemplateSites";
                DROP TRIGGER IF EXISTS "TR_ClientTemplateSites_GlobalWorkstationPolicyId" ON "ClientTemplateSites";
                DROP TRIGGER IF EXISTS "TR_ClientTemplateSites_GlobalServerPatchPolicyId" ON "ClientTemplateSites";
                DROP TRIGGER IF EXISTS "TR_ClientTemplateSites_GlobalWorkstationPatchPolicyId" ON "ClientTemplateSites";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_ClientTemplates_PatchPolicies_ServerPatchPolicyId",
                table: "ClientTemplates");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientTemplates_PatchPolicies_WorkstationPatchPolicyId",
                table: "ClientTemplates");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientTemplates_Policies_ServerPolicyId",
                table: "ClientTemplates");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientTemplates_Policies_WorkstationPolicyId",
                table: "ClientTemplates");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientTemplateSites_PatchPolicies_ServerPatchPolicyId",
                table: "ClientTemplateSites");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientTemplateSites_PatchPolicies_WorkstationPatchPolicyId",
                table: "ClientTemplateSites");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientTemplateSites_Policies_ServerPolicyId",
                table: "ClientTemplateSites");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientTemplateSites_Policies_WorkstationPolicyId",
                table: "ClientTemplateSites");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SitePolicies",
                table: "SitePolicies");

            migrationBuilder.DropIndex(
                name: "IX_SitePolicies_SiteId_ClientId",
                table: "SitePolicies");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SitePatchPolicies",
                table: "SitePatchPolicies");

            migrationBuilder.DropIndex(
                name: "IX_SitePatchPolicies_SiteId_ClientId",
                table: "SitePatchPolicies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Policies_DefaultForAll",
                table: "Policies");

            migrationBuilder.DropIndex(
                name: "IX_ClientTemplateSites_ServerPatchPolicyId",
                table: "ClientTemplateSites");

            migrationBuilder.DropIndex(
                name: "IX_ClientTemplateSites_ServerPolicyId",
                table: "ClientTemplateSites");

            migrationBuilder.DropIndex(
                name: "IX_ClientTemplateSites_WorkstationPatchPolicyId",
                table: "ClientTemplateSites");

            migrationBuilder.DropIndex(
                name: "IX_ClientTemplateSites_WorkstationPolicyId",
                table: "ClientTemplateSites");

            migrationBuilder.DropIndex(
                name: "IX_ClientTemplates_ServerPatchPolicyId",
                table: "ClientTemplates");

            migrationBuilder.DropIndex(
                name: "IX_ClientTemplates_ServerPolicyId",
                table: "ClientTemplates");

            migrationBuilder.DropIndex(
                name: "IX_ClientTemplates_WorkstationPatchPolicyId",
                table: "ClientTemplates");

            migrationBuilder.DropIndex(
                name: "IX_ClientTemplates_WorkstationPolicyId",
                table: "ClientTemplates");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ClientPolicies",
                table: "ClientPolicies");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ClientPatchPolicies",
                table: "ClientPatchPolicies");

            migrationBuilder.DropColumn(
                name: "AppliesTo",
                table: "SitePolicies");

            migrationBuilder.DropColumn(
                name: "AppliesTo",
                table: "SitePatchPolicies");

            migrationBuilder.DropColumn(
                name: "AppliesTo",
                table: "Policies");

            migrationBuilder.DropColumn(
                name: "AppliesTo",
                table: "PatchPolicies");

            migrationBuilder.DropColumn(
                name: "AppliesTo",
                table: "MonitoringTemplates");

            migrationBuilder.DropColumn(
                name: "ServerPatchPolicyId",
                table: "ClientTemplateSites");

            migrationBuilder.DropColumn(
                name: "ServerPolicyId",
                table: "ClientTemplateSites");

            migrationBuilder.DropColumn(
                name: "WorkstationPatchPolicyId",
                table: "ClientTemplateSites");

            migrationBuilder.DropColumn(
                name: "WorkstationPolicyId",
                table: "ClientTemplateSites");

            migrationBuilder.DropColumn(
                name: "ServerPatchPolicyId",
                table: "ClientTemplates");

            migrationBuilder.DropColumn(
                name: "ServerPolicyId",
                table: "ClientTemplates");

            migrationBuilder.DropColumn(
                name: "WorkstationPatchPolicyId",
                table: "ClientTemplates");

            migrationBuilder.DropColumn(
                name: "WorkstationPolicyId",
                table: "ClientTemplates");

            migrationBuilder.DropColumn(
                name: "AppliesTo",
                table: "ClientPolicies");

            migrationBuilder.DropColumn(
                name: "AppliesTo",
                table: "ClientPatchPolicies");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SitePolicies",
                table: "SitePolicies",
                column: "SiteId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SitePatchPolicies",
                table: "SitePatchPolicies",
                column: "SiteId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ClientPolicies",
                table: "ClientPolicies",
                column: "ClientId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ClientPatchPolicies",
                table: "ClientPatchPolicies",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_SitePolicies_SiteId_ClientId",
                table: "SitePolicies",
                columns: new[] { "SiteId", "ClientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SitePatchPolicies_SiteId_ClientId",
                table: "SitePatchPolicies",
                columns: new[] { "SiteId", "ClientId" },
                unique: true);
        }
    }
}
