using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <summary>
    /// Steps of tasks (0.6.0, decided 2026-10-15): a task runs an ordered list of library scripts and restarts, one after the other
    /// per endpoint. Expand only: every task gets its script as step 1 and keeps it in <c>AutomationTasks.ScriptId</c>, so the
    /// previous build keeps running on this schema. The workers may now write the job of a step of an enabled task, including a
    /// restart, and the jobs of a run started by hand, which carries the user who started it; fleeto-signer decides again.
    /// </summary>
    public partial class TaskSteps : Migration
    {
        /// <summary>A step belongs to its task's client, and runs a global script or one of that client.</summary>
        internal const string StepClientSql = """
            CREATE OR REPLACE FUNCTION fleeto_task_step_client() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE task_client uuid;
            DECLARE script_client uuid;
            BEGIN
              SELECT "ClientId" INTO task_client FROM "AutomationTasks" WHERE "Id" = NEW."AutomationTaskId";
              IF NEW."ClientId" IS DISTINCT FROM task_client THEN
                RAISE EXCEPTION 'A step belongs to the client of its task' USING ERRCODE = 'integrity_constraint_violation';
              END IF;
              IF NEW."ScriptId" IS NOT NULL THEN
                SELECT "ClientId" INTO script_client FROM "Scripts" WHERE "Id" = NEW."ScriptId";
                IF script_client IS NOT NULL AND script_client IS DISTINCT FROM task_client THEN
                  RAISE EXCEPTION 'A step can only run a global script or one of the client of its task' USING ERRCODE = 'integrity_constraint_violation';
                END IF;
              END IF;
              RETURN NEW;
            END $$;
            CREATE CONSTRAINT TRIGGER "TR_AutomationTaskSteps_Client" AFTER INSERT OR UPDATE ON "AutomationTaskSteps"
              FOR EACH ROW EXECUTE FUNCTION fleeto_task_step_client();
            """;

        /// <summary>Every task gets its script as its first step; runs that already ended keep that.</summary>
        internal const string BackfillSql = """
            INSERT INTO "AutomationTaskSteps" ("Id", "AutomationTaskId", "ClientId", "Position", "Kind", "ScriptId", "RunAs", "ContinueOnFailure",
                                               "SuccessExitCodes", "RestartWaitMinutes")
            SELECT gen_random_uuid(), t."Id", t."ClientId", 1, 'Script', t."ScriptId", t."RunAs", false, '{}', NULL FROM "AutomationTasks" t;

            UPDATE "AutomationTaskRunTargets" t
               SET "FinishedAt" = t."OutcomeHandledAt",
                   "Outcome" = CASE j."State" WHEN 'Succeeded' THEN 'Succeeded' WHEN 'Cancelled' THEN 'Cancelled' ELSE 'Failed' END
              FROM "Jobs" j
             WHERE j."Id" = t."JobId" AND t."OutcomeHandledAt" IS NOT NULL;
            """;

        /// <summary>
        /// Who may write which job (0.6.0, steps): the workers write only jobs without a user: the automatic installation of the
        /// Action1 agent, or the job of a step of an enabled task (its script and account, or a restart for a restart step) in a run
        /// of that task, which is a run Fleeto started or a run started by hand that names its user. A job without a step is the
        /// first and only script of a task as the previous build wrote it. Nobody else writes a job without a user or a restart.
        /// </summary>
        internal const string JobOriginSql = """
            CREATE OR REPLACE FUNCTION fleeto_job_origin() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF current_user = 'fleeto_workers' THEN
                IF NEW."InitiatedByUserId" <> '00000000-0000-0000-0000-000000000000' THEN
                  RAISE EXCEPTION 'The workers may only write a job without a user' USING ERRCODE = 'insufficient_privilege';
                ELSIF NEW."Type" = 'Action1Agent' THEN
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
                  RAISE EXCEPTION 'The workers may only write an automatic installation of the Action1 agent or the job of a step of an enabled task'
                    USING ERRCODE = 'insufficient_privilege';
                END IF;
              ELSIF current_user IN ('fleeto_web', 'fleeto_gateway', 'fleeto_signer')
                  AND (NEW."InitiatedByUserId" = '00000000-0000-0000-0000-000000000000' OR NEW."Type" = 'Restart') THEN
                RAISE EXCEPTION 'A job without a user or a restart may only be written by the workers' USING ERRCODE = 'insufficient_privilege';
              END IF;
              RETURN NEW;
            END $$;
            """;

        /// <summary>The rule of signing requests as of 0.6.0 with steps: the workers may also ask to sign the restart of a task.</summary>
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
                        AND (j."Type" = 'Action1Agent' OR (j."Type" IN ('Script', 'Restart') AND j."TaskId" IS NOT NULL))
                        AND j."InitiatedByUserId" = '00000000-0000-0000-0000-000000000000' AND j."State" = 'PendingSignature') THEN
                    RAISE EXCEPTION 'The workers may only request the signature of an automatic installation of the Action1 agent or of a job of a task'
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

        /// <summary>What a job asked stays as it is; its task, run and step may only become empty, when those are deleted.</summary>
        internal const string JobBindingSql = """
            CREATE OR REPLACE FUNCTION fleeto_job_binding() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF (NEW."Id", NEW."ClientId", NEW."EndpointId", NEW."BatchId", NEW."Type", NEW."ScriptId", NEW."ScriptVersionId", NEW."Language",
                  NEW."ScriptSha256", NEW."RunAs", NEW."RunAsUserId", NEW."CreatedAt", NEW."ValidUntil", NEW."InitiatedByUserId")
                 IS DISTINCT FROM
                 (OLD."Id", OLD."ClientId", OLD."EndpointId", OLD."BatchId", OLD."Type", OLD."ScriptId", OLD."ScriptVersionId", OLD."Language",
                  OLD."ScriptSha256", OLD."RunAs", OLD."RunAsUserId", OLD."CreatedAt", OLD."ValidUntil", OLD."InitiatedByUserId")
                 OR (NEW."TaskId" IS NOT NULL AND NEW."TaskId" IS DISTINCT FROM OLD."TaskId")
                 OR (NEW."TaskRunId" IS NOT NULL AND NEW."TaskRunId" IS DISTINCT FROM OLD."TaskRunId")
                 OR (NEW."TaskStepId" IS NOT NULL AND NEW."TaskStepId" IS DISTINCT FROM OLD."TaskStepId") THEN
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
        /// Who started a run (0.6.0, steps): only web writes a run started by hand, and only such a run names a user; the workers
        /// write the others. What started a run never changes afterwards.
        /// </summary>
        internal const string RunOriginSql = """
            CREATE OR REPLACE FUNCTION fleeto_task_run_origin() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF TG_OP = 'INSERT' THEN
                IF NEW."InitiatedByUserId" IS NOT NULL AND NEW."Trigger" <> 'Manual' THEN
                  RAISE EXCEPTION 'Only a run started by hand names a user' USING ERRCODE = 'insufficient_privilege';
                ELSIF current_user = 'fleeto_workers' AND NEW."Trigger" = 'Manual' THEN
                  RAISE EXCEPTION 'The workers do not write a run started by hand' USING ERRCODE = 'insufficient_privilege';
                ELSIF current_user = 'fleeto_web' AND NEW."Trigger" <> 'Manual' THEN
                  RAISE EXCEPTION 'Web writes only runs started by hand' USING ERRCODE = 'insufficient_privilege';
                END IF;
              ELSIF (NEW."Id", NEW."AutomationTaskId", NEW."ClientId", NEW."Trigger", NEW."OccurrenceAt", NEW."InitiatedByUserId", NEW."CreatedAt")
                    IS DISTINCT FROM
                    (OLD."Id", OLD."AutomationTaskId", OLD."ClientId", OLD."Trigger", OLD."OccurrenceAt", OLD."InitiatedByUserId", OLD."CreatedAt") THEN
                RAISE EXCEPTION 'What started a run cannot be changed' USING ERRCODE = 'insufficient_privilege';
              END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER "TR_AutomationTaskRuns_Origin" BEFORE INSERT OR UPDATE ON "AutomationTaskRuns"
              FOR EACH ROW EXECUTE FUNCTION fleeto_task_run_origin();
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TaskStepId",
                table: "Jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FinishedAt",
                table: "AutomationTaskRunTargets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "AutomationTaskRunTargets",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InitiatedByUserId",
                table: "AutomationTaskRuns",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AutomationTaskSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: true),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScriptId = table.Column<Guid>(type: "uuid", nullable: true),
                    RunAs = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ContinueOnFailure = table.Column<bool>(type: "boolean", nullable: false),
                    SuccessExitCodes = table.Column<List<int>>(type: "integer[]", nullable: false),
                    RestartWaitMinutes = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationTaskSteps", x => x.Id);
                    table.CheckConstraint("CK_AutomationTaskSteps_ExitCodes", "cardinality(\"SuccessExitCodes\") <= 10 AND NOT (0 = ANY (\"SuccessExitCodes\"))");
                    table.CheckConstraint("CK_AutomationTaskSteps_Kind", "(\"Kind\" = 'Script' AND \"ScriptId\" IS NOT NULL AND \"RestartWaitMinutes\" IS NULL) OR (\"Kind\" = 'Restart' AND \"ScriptId\" IS NULL AND \"RestartWaitMinutes\" BETWEEN 5 AND 240 AND cardinality(\"SuccessExitCodes\") = 0)");
                    table.CheckConstraint("CK_AutomationTaskSteps_Position", "\"Position\" >= 1");
                    table.ForeignKey(
                        name: "FK_AutomationTaskSteps_AutomationTasks_AutomationTaskId",
                        column: x => x.AutomationTaskId,
                        principalTable: "AutomationTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AutomationTaskSteps_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AutomationTaskSteps_Scripts_ScriptId",
                        column: x => x.ScriptId,
                        principalTable: "Scripts",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_TaskStep",
                table: "Jobs",
                columns: new[] { "TaskRunId", "EndpointId", "TaskStepId" },
                unique: true,
                filter: "\"TaskStepId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTaskRunTargets_Busy",
                table: "AutomationTaskRunTargets",
                columns: new[] { "EndpointId", "AutomationTaskId", "State" },
                filter: "\"State\" = 'Started' AND \"FinishedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTaskSteps_AutomationTaskId_Position",
                table: "AutomationTaskSteps",
                columns: new[] { "AutomationTaskId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTaskSteps_ClientId",
                table: "AutomationTaskSteps",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTaskSteps_ScriptId",
                table: "AutomationTaskSteps",
                column: "ScriptId");

            migrationBuilder.Sql(StepClientSql);
            migrationBuilder.Sql(BackfillSql);
            migrationBuilder.Sql(JobOriginSql);
            migrationBuilder.Sql(OriginFunctionSql);
            migrationBuilder.Sql(JobBindingSql);
            migrationBuilder.Sql(RunOriginSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_AutomationTaskRuns_Origin" ON "AutomationTaskRuns";
                DROP FUNCTION IF EXISTS fleeto_task_run_origin();
                DROP TRIGGER IF EXISTS "TR_AutomationTaskSteps_Client" ON "AutomationTaskSteps";
                DROP FUNCTION IF EXISTS fleeto_task_step_client();
                """);
            migrationBuilder.Sql(AutomationTasks.JobOriginSql);
            migrationBuilder.Sql(AutomationTasks.OriginFunctionSql);
            migrationBuilder.Sql(AutomationTasks.JobBindingSql);

            migrationBuilder.DropTable(
                name: "AutomationTaskSteps");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_TaskStep",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_AutomationTaskRunTargets_Busy",
                table: "AutomationTaskRunTargets");

            migrationBuilder.DropColumn(
                name: "TaskStepId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "FinishedAt",
                table: "AutomationTaskRunTargets");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "AutomationTaskRunTargets");

            migrationBuilder.DropColumn(
                name: "InitiatedByUserId",
                table: "AutomationTaskRuns");
        }
    }
}
