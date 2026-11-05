using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PatchRounds : Migration
    {
        /// <summary>
        /// The rule of jobs as of 0.9.0 step 4: the workers may also write the installation of a round of a patch policy of engine Fleeto
        /// (checked further by <c>fleeto_job_patch_origin</c>). Otherwise as <see cref="TaskSteps.JobOriginSql"/>.
        /// </summary>
        internal const string JobOriginSql = """
            CREATE OR REPLACE FUNCTION fleeto_job_origin() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF current_user = 'fleeto_workers' THEN
                IF NEW."InitiatedByUserId" <> '00000000-0000-0000-0000-000000000000' THEN
                  RAISE EXCEPTION 'The workers may only write a job without a user' USING ERRCODE = 'insufficient_privilege';
                ELSIF NEW."Type" IN ('Action1Agent', 'PatchInstall') THEN
                  NULL;
                ELSIF NEW."TaskId" IS NOT NULL AND NEW."TaskRunId" IS NOT NULL AND NEW."RunAsUserId" IS NULL
                    AND EXISTS (SELECT 1 FROM "AutomationTaskRuns" r WHERE r."Id" = NEW."TaskRunId" AND r."AutomationTaskId" = NEW."TaskId"
                                  AND r."ClientId" = NEW."ClientId" AND (r."Trigger" <> 'Manual' OR r."InitiatedByUserId" IS NOT NULL))
                    AND (
                      (NEW."TaskStepId" IS NULL AND NEW."Type" = 'Script'
                        AND EXISTS (SELECT 1 FROM "AutomationTasks" t WHERE t."Id" = NEW."TaskId" AND t."Enabled"
                                      AND t."ScriptId" = NEW."ScriptId" AND t."RunAs" = NEW."RunAs"
                                      AND (t."ClientId" IS NULL OR t."ClientId" = NEW."ClientId")))
                      OR (NEW."TaskStepId" IS NOT NULL
                        AND EXISTS (SELECT 1 FROM "AutomationTaskSteps" s JOIN "AutomationTasks" t ON t."Id" = s."AutomationTaskId"
                                      WHERE s."Id" = NEW."TaskStepId" AND t."Id" = NEW."TaskId" AND t."Enabled"
                                        AND (t."ClientId" IS NULL OR t."ClientId" = NEW."ClientId")
                                        AND ((s."Kind" = 'Script' AND NEW."Type" = 'Script' AND s."ScriptId" = NEW."ScriptId" AND s."RunAs" = NEW."RunAs")
                                          OR (s."Kind" = 'Restart' AND NEW."Type" = 'Restart' AND NEW."ScriptId" IS NULL))))) THEN
                  NULL;
                ELSE
                  RAISE EXCEPTION 'The workers may only write an automatic installation of the Action1 agent, a round of a patch policy or the job of a step of an enabled task'
                    USING ERRCODE = 'insufficient_privilege';
                END IF;
              ELSIF current_user IN ('fleeto_web', 'fleeto_gateway', 'fleeto_signer')
                  AND (NEW."InitiatedByUserId" = '00000000-0000-0000-0000-000000000000' OR NEW."Type" = 'Restart') THEN
                RAISE EXCEPTION 'A job without a user or a restart may only be written by the workers' USING ERRCODE = 'insufficient_privilege';
              END IF;
              RETURN NEW;
            END $$;
            """;

        /// <summary>The rule of signing requests as of 0.9.0 step 4: the workers may also ask to sign the installation of a round.</summary>
        internal const string OriginFunctionSql = """
            CREATE OR REPLACE FUNCTION fleeto_signing_request_origin() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."Kind" IN ('AgentEnrollment', 'AgentRenewal', 'GatewayCertificate', 'AgentRecovery', 'WatchdogCertificate') THEN
                IF current_user IN ('fleeto_web', 'fleeto_workers', 'fleeto_signer') THEN
                  RAISE EXCEPTION '% requests may only be created by the gateway', NEW."Kind" USING ERRCODE = 'insufficient_privilege';
                END IF;
              ELSIF NEW."Kind" = 'AgentConfig' THEN
                IF current_user IN ('fleeto_web', 'fleeto_gateway') THEN
                  RAISE EXCEPTION 'AgentConfig requests may only be created by the workers or the signer' USING ERRCODE = 'insufficient_privilege';
                END IF;
              ELSIF NEW."Kind" = 'Job' THEN
                IF current_user IN ('fleeto_gateway', 'fleeto_signer') THEN
                  RAISE EXCEPTION 'Job requests may only be created by web or the workers' USING ERRCODE = 'insufficient_privilege';
                ELSIF current_user = 'fleeto_workers' THEN
                  -- Nested, not AND: only the workers' request may look at the job.
                  IF NOT EXISTS (
                      SELECT 1 FROM "Jobs" j WHERE j."Id" = NEW."SubjectId" AND j."ClientId" = NEW."ClientId"
                        AND (j."Type" = 'Action1Agent' OR (j."Type" IN ('Script', 'Restart') AND j."TaskId" IS NOT NULL)
                             OR (j."Type" = 'PatchInstall' AND j."PatchDeploymentId" IS NOT NULL))
                        AND j."InitiatedByUserId" = '00000000-0000-0000-0000-000000000000' AND j."State" = 'PendingSignature') THEN
                    RAISE EXCEPTION 'The workers may only request the signature of an automatic installation of the Action1 agent, of a round or of a job of a task'
                      USING ERRCODE = 'insufficient_privilege';
                  END IF;
                END IF;
              ELSIF NEW."Kind" IN ('RemoteSessionToken', 'AgentUninstall') THEN
                IF current_user IN ('fleeto_gateway', 'fleeto_workers', 'fleeto_signer') THEN
                  RAISE EXCEPTION '% requests may only be created by web', NEW."Kind" USING ERRCODE = 'insufficient_privilege';
                END IF;
              ELSIF current_user IN ('fleeto_web', 'fleeto_workers', 'fleeto_signer', 'fleeto_gateway') THEN
                RAISE EXCEPTION 'Unknown signing request kind %', NEW."Kind" USING ERRCODE = 'insufficient_privilege';
              END IF;
              RETURN NEW;
            END $$;
            """;

        /// <summary>
        /// The origin of an installation job as of 0.9.0 step 4: web writes it for a deployment of engine Fleeto the same user started (as in
        /// step 3); the workers write it, without a user, only for an endpoint of a round of an enabled patch policy of engine Fleeto (scope
        /// PatchPolicy, no user, the policy named). fleeto-signer checks the rest again before it signs.
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
              IF current_user IN ('fleeto_gateway', 'fleeto_signer') THEN
                RAISE EXCEPTION 'An installation job may only be written by web or the workers' USING ERRCODE = 'insufficient_privilege';
              END IF;
              IF NEW."PatchDeploymentId" IS NULL OR NEW."PatchDeploymentTargetId" IS NULL OR NEW."ScriptId" IS NOT NULL
                 OR NEW."TaskId" IS NOT NULL THEN
                RAISE EXCEPTION 'An installation job must name an endpoint of a deployment by Fleeto' USING ERRCODE = 'insufficient_privilege';
              END IF;
              -- Without a user it is a round (fleeto_job_origin lets only the workers write such a job); with one, a deployment by hand.
              IF NEW."InitiatedByUserId" = '00000000-0000-0000-0000-000000000000' THEN
                IF current_user = 'fleeto_web' OR NOT EXISTS (
                     SELECT 1 FROM "PatchDeploymentTargets" t JOIN "PatchDeployments" d ON d."Id" = t."DeploymentId"
                       JOIN "PatchPolicies" p ON p."Id" = d."PatchPolicyId"
                     WHERE t."Id" = NEW."PatchDeploymentTargetId" AND d."Id" = NEW."PatchDeploymentId"
                       AND t."EndpointId" = NEW."EndpointId" AND d."ClientId" = NEW."ClientId" AND t."ClientId" = NEW."ClientId"
                       AND d."Engine" = 'Fleeto' AND d."Scope" = 'PatchPolicy' AND d."RoundAt" IS NOT NULL
                       AND d."RequestedByUserId" = '00000000-0000-0000-0000-000000000000'
                       AND p."Enabled" AND p."Engine" = 'Fleeto' AND (p."ClientId" IS NULL OR p."ClientId" = NEW."ClientId")) THEN
                  RAISE EXCEPTION 'The workers may only write the installation of an endpoint of a round of an enabled patch policy by Fleeto'
                    USING ERRCODE = 'insufficient_privilege';
                END IF;
              ELSIF current_user = 'fleeto_workers' OR NOT EXISTS (
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
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IncludeDrivers",
                table: "PatchPolicies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "InstallTimeoutMinutes",
                table: "PatchPolicies",
                type: "integer",
                nullable: false,
                defaultValue: 240);

            migrationBuilder.AddColumn<bool>(
                name: "LinuxSecurityOnly",
                table: "PatchPolicies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "MicrosoftUpdate",
                table: "PatchPolicies",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "RandomDelayMinutes",
                table: "PatchPolicies",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<bool>(
                name: "TurnOffDistributionUpdates",
                table: "PatchPolicies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DueAt",
                table: "PatchDeploymentTargets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RoundAt",
                table: "PatchDeployments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DistributionUpdates",
                table: "InventorySnapshots",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                table: "InventorySnapshots",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "PatchRoundStates",
                columns: table => new
                {
                    PatchPolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ScheduleHash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatchRoundStates", x => x.PatchPolicyId);
                    table.ForeignKey(
                        name: "FK_PatchRoundStates_PatchPolicies_PatchPolicyId",
                        column: x => x.PatchPolicyId,
                        principalTable: "PatchPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_PatchPolicies_InstallTimeoutMinutes",
                table: "PatchPolicies",
                sql: "\"InstallTimeoutMinutes\" BETWEEN 30 AND 1440");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PatchPolicies_RandomDelayMinutes",
                table: "PatchPolicies",
                sql: "\"RandomDelayMinutes\" BETWEEN 0 AND 720");

            migrationBuilder.CreateIndex(
                name: "IX_PatchDeploymentTargets_DueAt",
                table: "PatchDeploymentTargets",
                column: "DueAt",
                filter: "\"DueAt\" IS NOT NULL AND \"JobId\" IS NULL AND \"State\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_PatchDeployments_ClientId_PatchPolicyId_RoundAt",
                table: "PatchDeployments",
                columns: new[] { "ClientId", "PatchPolicyId", "RoundAt" },
                unique: true,
                filter: "\"RoundAt\" IS NOT NULL");

            migrationBuilder.Sql(JobOriginSql);
            migrationBuilder.Sql(OriginFunctionSql);
            migrationBuilder.Sql(PatchJobSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PatchInstallJobs.PatchJobSql);
            migrationBuilder.Sql(TaskSteps.OriginFunctionSql);
            migrationBuilder.Sql(TaskSteps.JobOriginSql);

            migrationBuilder.DropTable(
                name: "PatchRoundStates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PatchPolicies_InstallTimeoutMinutes",
                table: "PatchPolicies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PatchPolicies_RandomDelayMinutes",
                table: "PatchPolicies");

            migrationBuilder.DropIndex(
                name: "IX_PatchDeploymentTargets_DueAt",
                table: "PatchDeploymentTargets");

            migrationBuilder.DropIndex(
                name: "IX_PatchDeployments_ClientId_PatchPolicyId_RoundAt",
                table: "PatchDeployments");

            migrationBuilder.DropColumn(
                name: "IncludeDrivers",
                table: "PatchPolicies");

            migrationBuilder.DropColumn(
                name: "InstallTimeoutMinutes",
                table: "PatchPolicies");

            migrationBuilder.DropColumn(
                name: "LinuxSecurityOnly",
                table: "PatchPolicies");

            migrationBuilder.DropColumn(
                name: "MicrosoftUpdate",
                table: "PatchPolicies");

            migrationBuilder.DropColumn(
                name: "RandomDelayMinutes",
                table: "PatchPolicies");

            migrationBuilder.DropColumn(
                name: "TurnOffDistributionUpdates",
                table: "PatchPolicies");

            migrationBuilder.DropColumn(
                name: "DueAt",
                table: "PatchDeploymentTargets");

            migrationBuilder.DropColumn(
                name: "RoundAt",
                table: "PatchDeployments");

            migrationBuilder.DropColumn(
                name: "DistributionUpdates",
                table: "InventorySnapshots");

            migrationBuilder.DropColumn(
                name: "TimeZone",
                table: "InventorySnapshots");
        }
    }
}
