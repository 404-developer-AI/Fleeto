using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PatchInstallJobs : Migration
    {

        /// <summary>
        /// The origin of an installation job by Fleeto (0.9.0 step 3): it is bound to an endpoint of a deployment of engine Fleeto of the
        /// same client and endpoint, web writes it only for a deployment the same user started (not a round of a patch policy), the workers
        /// not yet (rounds follow in step 4), and no other job carries a deployment. The binding never changes afterwards.
        /// </summary>
        internal const string PatchJobSql = """
            CREATE OR REPLACE FUNCTION fleeto_job_patch_origin() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."Type" <> 'PatchInstall' THEN
                IF NEW."PatchDeploymentId" IS NOT NULL OR NEW."PatchDeploymentTargetId" IS NOT NULL THEN
                  RAISE EXCEPTION 'Only an installation job is bound to a deployment' USING ERRCODE = 'insufficient_privilege';
                END IF;
                RETURN NEW;
              END IF;
              IF current_user IN ('fleeto_gateway', 'fleeto_signer', 'fleeto_workers') THEN
                RAISE EXCEPTION 'An installation job may only be written by web for a deployment a technician started'
                  USING ERRCODE = 'insufficient_privilege';
              END IF;
              IF NEW."PatchDeploymentId" IS NULL OR NEW."PatchDeploymentTargetId" IS NULL OR NEW."ScriptId" IS NOT NULL
                 OR NEW."TaskId" IS NOT NULL OR NOT EXISTS (
                   SELECT 1 FROM "PatchDeploymentTargets" t JOIN "PatchDeployments" d ON d."Id" = t."DeploymentId"
                   WHERE t."Id" = NEW."PatchDeploymentTargetId" AND d."Id" = NEW."PatchDeploymentId"
                     AND t."EndpointId" = NEW."EndpointId" AND d."ClientId" = NEW."ClientId" AND t."ClientId" = NEW."ClientId"
                     AND d."Engine" = 'Fleeto' AND d."Scope" IN ('AllMissing', 'Specified')
                     AND d."RequestedByUserId" = NEW."InitiatedByUserId") THEN
                RAISE EXCEPTION 'An installation job must name an endpoint of a deployment by Fleeto that its user started'
                  USING ERRCODE = 'insufficient_privilege';
              END IF;
              RETURN NEW;
            END $$;
            DROP TRIGGER IF EXISTS "TR_Jobs_PatchOrigin" ON "Jobs";
            CREATE TRIGGER "TR_Jobs_PatchOrigin" BEFORE INSERT ON "Jobs" FOR EACH ROW EXECUTE FUNCTION fleeto_job_patch_origin();

            CREATE OR REPLACE FUNCTION fleeto_job_patch_binding() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."PatchDeploymentId" IS DISTINCT FROM OLD."PatchDeploymentId"
                 OR NEW."PatchDeploymentTargetId" IS DISTINCT FROM OLD."PatchDeploymentTargetId" THEN
                RAISE EXCEPTION 'job %: its deployment cannot be changed', OLD."Id" USING ERRCODE = 'insufficient_privilege';
              END IF;
              RETURN NEW;
            END $$;
            DROP TRIGGER IF EXISTS "TR_Jobs_PatchBinding" ON "Jobs";
            CREATE TRIGGER "TR_Jobs_PatchBinding" BEFORE UPDATE ON "Jobs" FOR EACH ROW EXECUTE FUNCTION fleeto_job_patch_binding();
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "JobId",
                table: "PatchDeploymentTargets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Restart",
                table: "PatchDeploymentTargets",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Engine",
                table: "PatchDeployments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Action1");

            migrationBuilder.AddColumn<bool>(
                name: "SecurityOnly",
                table: "PatchDeployments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "PatchDeploymentId",
                table: "Jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PatchDeploymentTargetId",
                table: "Jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatchDeploymentTargets_JobId",
                table: "PatchDeploymentTargets",
                column: "JobId",
                filter: "\"JobId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PatchDeployments_Engine",
                table: "PatchDeployments",
                sql: "\"Engine\" IN ('Action1', 'Fleeto')");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_PatchDeploymentId",
                table: "Jobs",
                column: "PatchDeploymentId",
                filter: "\"PatchDeploymentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_PatchDeploymentTargetId",
                table: "Jobs",
                column: "PatchDeploymentTargetId",
                unique: true,
                filter: "\"PatchDeploymentTargetId\" IS NOT NULL");

            migrationBuilder.Sql(PatchJobSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_Jobs_PatchOrigin" ON "Jobs";
                DROP TRIGGER IF EXISTS "TR_Jobs_PatchBinding" ON "Jobs";
                DROP FUNCTION IF EXISTS fleeto_job_patch_origin();
                DROP FUNCTION IF EXISTS fleeto_job_patch_binding();
                """);

            migrationBuilder.DropIndex(
                name: "IX_PatchDeploymentTargets_JobId",
                table: "PatchDeploymentTargets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PatchDeployments_Engine",
                table: "PatchDeployments");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_PatchDeploymentId",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_PatchDeploymentTargetId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "JobId",
                table: "PatchDeploymentTargets");

            migrationBuilder.DropColumn(
                name: "Restart",
                table: "PatchDeploymentTargets");

            migrationBuilder.DropColumn(
                name: "Engine",
                table: "PatchDeployments");

            migrationBuilder.DropColumn(
                name: "SecurityOnly",
                table: "PatchDeployments");

            migrationBuilder.DropColumn(
                name: "PatchDeploymentId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "PatchDeploymentTargetId",
                table: "Jobs");
        }
    }
}
