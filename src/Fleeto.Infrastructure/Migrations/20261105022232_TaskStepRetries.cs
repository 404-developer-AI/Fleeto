using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TaskStepRetries : Migration
    {
        /// <summary>
        /// The rule of jobs as of 0.9.0 (a step run again): a job of the workers that runs a step again names a retry a user asked for, of
        /// the same run, endpoint and step, that has no job yet. Nobody else writes a job with a retry. Otherwise as
        /// <see cref="PatchRounds.JobOriginSql"/>.
        /// </summary>
        internal const string JobOriginSql = """
            CREATE OR REPLACE FUNCTION fleeto_job_origin() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF current_user = 'fleeto_workers' THEN
                IF NEW."InitiatedByUserId" <> '00000000-0000-0000-0000-000000000000' THEN
                  RAISE EXCEPTION 'The workers may only write a job without a user' USING ERRCODE = 'insufficient_privilege';
                ELSIF NEW."TaskRetryId" IS NOT NULL AND (NEW."TaskStepId" IS NULL OR NEW."Type" NOT IN ('Script', 'Restart') OR NOT EXISTS (
                    SELECT 1 FROM "AutomationTaskStepRetries" x WHERE x."Id" = NEW."TaskRetryId" AND x."RunId" = NEW."TaskRunId"
                      AND x."EndpointId" = NEW."EndpointId" AND x."ClientId" = NEW."ClientId" AND x."AutomationTaskId" = NEW."TaskId"
                      AND x."TaskStepId" = NEW."TaskStepId" AND x."JobId" IS NULL AND x."FinishedAt" IS NULL)) THEN
                  RAISE EXCEPTION 'The workers may only run a step again for a retry a user asked for' USING ERRCODE = 'insufficient_privilege';
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
                  AND (NEW."InitiatedByUserId" = '00000000-0000-0000-0000-000000000000' OR NEW."Type" = 'Restart' OR NEW."TaskRetryId" IS NOT NULL) THEN
                RAISE EXCEPTION 'A job without a user, a restart or a step run again may only be written by the workers' USING ERRCODE = 'insufficient_privilege';
              END IF;
              RETURN NEW;
            END $$;
            """;

        /// <summary>As <see cref="ScriptExitCodes.JobBindingSql"/>, and the retry a job runs (0.9.0) may only become empty, when it is deleted.</summary>
        internal const string JobBindingSql = """
            CREATE OR REPLACE FUNCTION fleeto_job_binding() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF (NEW."Id", NEW."ClientId", NEW."EndpointId", NEW."BatchId", NEW."Type", NEW."Language",
                  NEW."ScriptSha256", NEW."RunAs", NEW."RunAsUserId", NEW."CreatedAt", NEW."ValidUntil", NEW."InitiatedByUserId")
                 IS DISTINCT FROM
                 (OLD."Id", OLD."ClientId", OLD."EndpointId", OLD."BatchId", OLD."Type", OLD."Language",
                  OLD."ScriptSha256", OLD."RunAs", OLD."RunAsUserId", OLD."CreatedAt", OLD."ValidUntil", OLD."InitiatedByUserId")
                 OR (NEW."ScriptId" IS NOT NULL AND NEW."ScriptId" IS DISTINCT FROM OLD."ScriptId")
                 OR (NEW."ScriptVersionId" IS NOT NULL AND NEW."ScriptVersionId" IS DISTINCT FROM OLD."ScriptVersionId")
                 OR (NEW."TaskId" IS NOT NULL AND NEW."TaskId" IS DISTINCT FROM OLD."TaskId")
                 OR (NEW."TaskRunId" IS NOT NULL AND NEW."TaskRunId" IS DISTINCT FROM OLD."TaskRunId")
                 OR (NEW."TaskStepId" IS NOT NULL AND NEW."TaskStepId" IS DISTINCT FROM OLD."TaskStepId")
                 OR (NEW."TaskRetryId" IS NOT NULL AND NEW."TaskRetryId" IS DISTINCT FROM OLD."TaskRetryId")
                 OR (NEW."ExitStatus" IS DISTINCT FROM OLD."ExitStatus"
                     AND NOT (OLD."ExitStatus" IS NULL AND OLD."CompletedAt" IS NULL AND NEW."CompletedAt" IS NOT NULL)) THEN
                RAISE EXCEPTION 'job %: what was requested cannot be changed', OLD."Id" USING ERRCODE = 'insufficient_privilege';
              END IF;
              IF NEW."State" = 'PendingSignature' AND OLD."State" <> 'PendingSignature' THEN
                RAISE EXCEPTION 'job %: a job never waits for a signature again', OLD."Id" USING ERRCODE = 'insufficient_privilege';
              END IF;
              IF (OLD."Payload" IS NOT NULL AND NEW."Payload" IS DISTINCT FROM OLD."Payload")
                 OR (OLD."Signature" IS NOT NULL AND NEW."Signature" IS DISTINCT FROM OLD."Signature") THEN
                RAISE EXCEPTION 'job %: a signed job cannot be changed', OLD."Id" USING ERRCODE = 'insufficient_privilege';
              END IF;
              RETURN NEW;
            END $$;
            """;

        /// <summary>
        /// Who asks to run a step again (0.9.0): only web, for a user, for a step of the task of the run, on an endpoint of that run. What
        /// was asked never changes; the workers add the job once and close the retry, and a closed retry stays closed.
        /// </summary>
        internal const string RetryOriginSql = """
            CREATE OR REPLACE FUNCTION fleeto_task_step_retry_origin() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF TG_OP = 'INSERT' THEN
                IF current_user IN ('fleeto_workers', 'fleeto_gateway', 'fleeto_signer') THEN
                  RAISE EXCEPTION 'Only web asks to run a step again' USING ERRCODE = 'insufficient_privilege';
                ELSIF NEW."RequestedByUserId" = '00000000-0000-0000-0000-000000000000' OR NEW."JobId" IS NOT NULL OR NEW."HandledAt" IS NOT NULL
                    OR NEW."FinishedAt" IS NOT NULL OR NEW."Reason" IS NOT NULL THEN
                  RAISE EXCEPTION 'A step run again names a user and starts without a job' USING ERRCODE = 'insufficient_privilege';
                ELSIF NOT EXISTS (SELECT 1 FROM "AutomationTaskRunTargets" t WHERE t."RunId" = NEW."RunId" AND t."EndpointId" = NEW."EndpointId"
                                    AND t."ClientId" = NEW."ClientId" AND t."AutomationTaskId" = NEW."AutomationTaskId")
                    OR NOT EXISTS (SELECT 1 FROM "AutomationTaskSteps" s WHERE s."Id" = NEW."TaskStepId" AND s."AutomationTaskId" = NEW."AutomationTaskId") THEN
                  RAISE EXCEPTION 'A step run again belongs to the task of its run' USING ERRCODE = 'insufficient_privilege';
                END IF;
              ELSIF (NEW."Id", NEW."RunId", NEW."EndpointId", NEW."ClientId", NEW."AutomationTaskId", NEW."TaskStepId", NEW."RequestedByUserId",
                     NEW."RequestedByName", NEW."RequestedAt")
                    IS DISTINCT FROM
                    (OLD."Id", OLD."RunId", OLD."EndpointId", OLD."ClientId", OLD."AutomationTaskId", OLD."TaskStepId", OLD."RequestedByUserId",
                     OLD."RequestedByName", OLD."RequestedAt")
                  OR (OLD."JobId" IS NOT NULL AND NEW."JobId" IS NOT NULL AND NEW."JobId" <> OLD."JobId")
                  OR (OLD."FinishedAt" IS NOT NULL AND NEW."FinishedAt" IS NULL) THEN
                RAISE EXCEPTION 'What was asked of a step run again cannot be changed' USING ERRCODE = 'insufficient_privilege';
              END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER "TR_AutomationTaskStepRetries_Origin" BEFORE INSERT OR UPDATE ON "AutomationTaskStepRetries"
              FOR EACH ROW EXECUTE FUNCTION fleeto_task_step_retry_origin();
            """;
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jobs_TaskStep",
                table: "Jobs");

            migrationBuilder.AddColumn<Guid>(
                name: "TaskRetryId",
                table: "Jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AutomationTaskStepRetries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskStepId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    HandledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationTaskStepRetries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationTaskStepRetries_AutomationTaskRunTargets_RunId_En~",
                        columns: x => new { x.RunId, x.EndpointId },
                        principalTable: "AutomationTaskRunTargets",
                        principalColumns: new[] { "RunId", "EndpointId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AutomationTaskStepRetries_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AutomationTaskStepRetries_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_TaskRetryId",
                table: "Jobs",
                column: "TaskRetryId",
                unique: true,
                filter: "\"TaskRetryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_TaskStep",
                table: "Jobs",
                columns: new[] { "TaskRunId", "EndpointId", "TaskStepId" },
                unique: true,
                filter: "\"TaskStepId\" IS NOT NULL AND \"TaskRetryId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTaskStepRetries_EndpointId_ClientId",
                table: "AutomationTaskStepRetries",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTaskStepRetries_JobId",
                table: "AutomationTaskStepRetries",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTaskStepRetries_Open",
                table: "AutomationTaskStepRetries",
                columns: new[] { "RunId", "EndpointId" },
                unique: true,
                filter: "\"FinishedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTaskStepRetries_Waiting",
                table: "AutomationTaskStepRetries",
                column: "RequestedAt",
                filter: "\"HandledAt\" IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_AutomationTaskStepRetries_TaskRetryId",
                table: "Jobs",
                column: "TaskRetryId",
                principalTable: "AutomationTaskStepRetries",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.Sql(JobOriginSql);
            migrationBuilder.Sql(JobBindingSql);
            migrationBuilder.Sql(RetryOriginSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PatchRounds.JobOriginSql);
            migrationBuilder.Sql(ScriptExitCodes.JobBindingSql);
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_AutomationTaskStepRetries_Origin" ON "AutomationTaskStepRetries";
                DROP FUNCTION IF EXISTS fleeto_task_step_retry_origin();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_AutomationTaskStepRetries_TaskRetryId",
                table: "Jobs");

            migrationBuilder.DropTable(
                name: "AutomationTaskStepRetries");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_TaskRetryId",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_TaskStep",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "TaskRetryId",
                table: "Jobs");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_TaskStep",
                table: "Jobs",
                columns: new[] { "TaskRunId", "EndpointId", "TaskStepId" },
                unique: true,
                filter: "\"TaskStepId\" IS NOT NULL");
        }
    }
}
