using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <summary>
    /// Tasks (0.6.0): library scripts that run by themselves, their links on three levels, the runs and their endpoints, and
    /// the moment an endpoint became managed. Additive: the previous release keeps running, because it writes no job with a
    /// task and nothing it writes changes. From here the workers may also write a job without a user for a run of an enabled
    /// task, with that task's script and account, and ask for its signature; fleeto-signer decides again before it signs.
    /// </summary>
    public partial class AutomationTasks : Migration
    {
        /// <summary>
        /// Who may write which job (0.6.0, tasks): a job without a user is the automatic installation of the Action1 agent or a job
        /// of a run of an enabled task with the task's script and account, and only the workers write it; they write no other job.
        /// </summary>
        internal const string JobOriginSql = """
            CREATE OR REPLACE FUNCTION fleeto_job_origin() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF current_user = 'fleeto_workers' THEN
                IF NEW."InitiatedByUserId" <> '00000000-0000-0000-0000-000000000000' THEN
                  RAISE EXCEPTION 'The workers may only write a job without a user' USING ERRCODE = 'insufficient_privilege';
                ELSIF NEW."Type" = 'Action1Agent' THEN
                  NULL;
                ELSIF NEW."Type" = 'Script' AND NEW."TaskId" IS NOT NULL AND NEW."TaskRunId" IS NOT NULL AND NEW."RunAsUserId" IS NULL
                    AND EXISTS (SELECT 1 FROM "AutomationTasks" t WHERE t."Id" = NEW."TaskId" AND t."Enabled"
                                  AND t."ScriptId" = NEW."ScriptId" AND t."RunAs" = NEW."RunAs"
                                  AND (t."ClientId" IS NULL OR t."ClientId" = NEW."ClientId"))
                    AND EXISTS (SELECT 1 FROM "AutomationTaskRuns" r WHERE r."Id" = NEW."TaskRunId" AND r."AutomationTaskId" = NEW."TaskId"
                                  AND r."ClientId" = NEW."ClientId" AND r."Trigger" <> 'Manual') THEN
                  NULL;
                ELSE
                  RAISE EXCEPTION 'The workers may only write an automatic installation of the Action1 agent or a job of a run of an enabled task'
                    USING ERRCODE = 'insufficient_privilege';
                END IF;
              ELSIF current_user IN ('fleeto_web', 'fleeto_gateway', 'fleeto_signer')
                  AND NEW."InitiatedByUserId" = '00000000-0000-0000-0000-000000000000' THEN
                RAISE EXCEPTION 'A job without a user may only be written by the workers' USING ERRCODE = 'insufficient_privilege';
              END IF;
              RETURN NEW;
            END $$;
            """;

        /// <summary>The rule as of 0.6.0 with tasks: every <c>SigningRequestKind</c> with the roles that may create it.</summary>
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
                        AND (j."Type" = 'Action1Agent' OR (j."Type" = 'Script' AND j."TaskId" IS NOT NULL))
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

        /// <summary>
        /// What a job asked stays as it is (0.2.1), now with the task and run of a job: they may only become empty, when the task
        /// or the run is deleted.
        /// </summary>
        internal const string JobBindingSql = """
            CREATE OR REPLACE FUNCTION fleeto_job_binding() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF (NEW."Id", NEW."ClientId", NEW."EndpointId", NEW."BatchId", NEW."Type", NEW."ScriptId", NEW."ScriptVersionId", NEW."Language",
                  NEW."ScriptSha256", NEW."RunAs", NEW."RunAsUserId", NEW."CreatedAt", NEW."ValidUntil", NEW."InitiatedByUserId")
                 IS DISTINCT FROM
                 (OLD."Id", OLD."ClientId", OLD."EndpointId", OLD."BatchId", OLD."Type", OLD."ScriptId", OLD."ScriptVersionId", OLD."Language",
                  OLD."ScriptSha256", OLD."RunAs", OLD."RunAsUserId", OLD."CreatedAt", OLD."ValidUntil", OLD."InitiatedByUserId")
                 OR (NEW."TaskId" IS NOT NULL AND NEW."TaskId" IS DISTINCT FROM OLD."TaskId")
                 OR (NEW."TaskRunId" IS NOT NULL AND NEW."TaskRunId" IS DISTINCT FROM OLD."TaskRunId") THEN
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

        /// <summary>The binding as 0.2.1 wrote it, for Down.</summary>
        private const string PreviousJobBindingSql = """
            CREATE OR REPLACE FUNCTION fleeto_job_binding() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF (NEW."Id", NEW."ClientId", NEW."EndpointId", NEW."BatchId", NEW."Type", NEW."ScriptId", NEW."ScriptVersionId", NEW."Language",
                  NEW."ScriptSha256", NEW."RunAs", NEW."RunAsUserId", NEW."CreatedAt", NEW."ValidUntil", NEW."InitiatedByUserId")
                 IS DISTINCT FROM
                 (OLD."Id", OLD."ClientId", OLD."EndpointId", OLD."BatchId", OLD."Type", OLD."ScriptId", OLD."ScriptVersionId", OLD."Language",
                  OLD."ScriptSha256", OLD."RunAs", OLD."RunAsUserId", OLD."CreatedAt", OLD."ValidUntil", OLD."InitiatedByUserId") THEN
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
        /// The client boundaries of tasks: a link never reaches a task of another client, a task of a client runs a global
        /// script or one of its own client, a global task a global script, and the jobs and endpoints of a run belong to the
        /// task's client (or any, for a global task).
        /// </summary>
        internal const string ClientRulesSql = """
            CREATE CONSTRAINT TRIGGER "TR_ClientAutomationTasks_Client" AFTER INSERT OR UPDATE ON "ClientAutomationTasks"
              FOR EACH ROW EXECUTE FUNCTION fleeto_link_same_client('AutomationTasks', 'AutomationTaskId', 'task');
            CREATE CONSTRAINT TRIGGER "TR_SiteAutomationTasks_Client" AFTER INSERT OR UPDATE ON "SiteAutomationTasks"
              FOR EACH ROW EXECUTE FUNCTION fleeto_link_same_client('AutomationTasks', 'AutomationTaskId', 'task');
            CREATE CONSTRAINT TRIGGER "TR_EndpointAutomationTasks_Client" AFTER INSERT OR UPDATE ON "EndpointAutomationTasks"
              FOR EACH ROW EXECUTE FUNCTION fleeto_link_same_client('AutomationTasks', 'AutomationTaskId', 'task');
            CREATE CONSTRAINT TRIGGER "TR_AutomationTaskRuns_Client" AFTER INSERT OR UPDATE ON "AutomationTaskRuns"
              FOR EACH ROW EXECUTE FUNCTION fleeto_link_same_client('AutomationTasks', 'AutomationTaskId', 'task');

            CREATE OR REPLACE FUNCTION fleeto_task_script_client() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE script_client uuid;
            BEGIN
              SELECT "ClientId" INTO script_client FROM "Scripts" WHERE "Id" = NEW."ScriptId";
              IF script_client IS NOT NULL AND script_client IS DISTINCT FROM NEW."ClientId" THEN
                RAISE EXCEPTION 'A task can only run a global script or one of its own client' USING ERRCODE = 'integrity_constraint_violation';
              END IF;
              RETURN NEW;
            END $$;
            CREATE CONSTRAINT TRIGGER "TR_AutomationTasks_ScriptClient" AFTER INSERT OR UPDATE OF "ClientId", "ScriptId" ON "AutomationTasks"
              FOR EACH ROW EXECUTE FUNCTION fleeto_task_script_client();
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TaskId",
                table: "Jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaskRunId",
                table: "Jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ManagedSince",
                table: "Endpoints",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AutomationTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ScriptId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunAs = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AppliesTo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ScheduleKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    StartMinute = table.Column<int>(type: "integer", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Every = table.Column<int>(type: "integer", nullable: false),
                    WeekDays = table.Column<int>(type: "integer", nullable: false),
                    MonthDay = table.Column<int>(type: "integer", nullable: false),
                    RepeatMinutes = table.Column<int>(type: "integer", nullable: true),
                    TriggerCheckIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    OnManaged = table.Column<bool>(type: "boolean", nullable: false),
                    RandomDelayMinutes = table.Column<int>(type: "integer", nullable: false),
                    CatchUp = table.Column<bool>(type: "boolean", nullable: false),
                    Overlap = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AlertOnFailure = table.Column<bool>(type: "boolean", nullable: false),
                    AlertSeverity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CopiedFromId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationTasks", x => x.Id);
                    table.CheckConstraint("CK_AutomationTasks_Dates", "\"EndDate\" IS NULL OR \"EndDate\" >= \"StartDate\"");
                    table.CheckConstraint("CK_AutomationTasks_Delay", "\"RandomDelayMinutes\" BETWEEN 0 AND 720");
                    table.CheckConstraint("CK_AutomationTasks_Every", "\"Every\" BETWEEN 1 AND 365");
                    table.CheckConstraint("CK_AutomationTasks_MonthDay", "\"MonthDay\" BETWEEN 1 AND 31");
                    table.CheckConstraint("CK_AutomationTasks_Repeat", "\"RepeatMinutes\" IS NULL OR \"RepeatMinutes\" BETWEEN 15 AND 720");
                    table.CheckConstraint("CK_AutomationTasks_StartMinute", "\"StartMinute\" BETWEEN 0 AND 1439");
                    table.CheckConstraint("CK_AutomationTasks_TriggerChecks", "cardinality(\"TriggerCheckIds\") <= 20");
                    table.CheckConstraint("CK_AutomationTasks_WeekDays", "\"WeekDays\" BETWEEN 0 AND 127");
                    table.ForeignKey(
                        name: "FK_AutomationTasks_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AutomationTasks_Scripts_ScriptId",
                        column: x => x.ScriptId,
                        principalTable: "Scripts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "AutomationTaskRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OccurrenceAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InitiatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CheckName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationTaskRuns", x => x.Id);
                    table.UniqueConstraint("AK_AutomationTaskRuns_Id_ClientId", x => new { x.Id, x.ClientId });
                    table.ForeignKey(
                        name: "FK_AutomationTaskRuns_AutomationTasks_AutomationTaskId",
                        column: x => x.AutomationTaskId,
                        principalTable: "AutomationTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AutomationTaskRuns_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutomationTaskStates",
                columns: table => new
                {
                    AutomationTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastOccurrenceAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextOccurrenceAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AlertsSeenUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ManagedSeenUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ScheduleHash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationTaskStates", x => x.AutomationTaskId);
                    table.ForeignKey(
                        name: "FK_AutomationTaskStates_AutomationTasks_AutomationTaskId",
                        column: x => x.AutomationTaskId,
                        principalTable: "AutomationTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientAutomationTasks",
                columns: table => new
                {
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientAutomationTasks", x => new { x.ClientId, x.AutomationTaskId });
                    table.ForeignKey(
                        name: "FK_ClientAutomationTasks_AutomationTasks_AutomationTaskId",
                        column: x => x.AutomationTaskId,
                        principalTable: "AutomationTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientAutomationTasks_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EndpointAutomationTasks",
                columns: table => new
                {
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndpointAutomationTasks", x => new { x.EndpointId, x.AutomationTaskId });
                    table.ForeignKey(
                        name: "FK_EndpointAutomationTasks_AutomationTasks_AutomationTaskId",
                        column: x => x.AutomationTaskId,
                        principalTable: "AutomationTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EndpointAutomationTasks_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SiteAutomationTasks",
                columns: table => new
                {
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteAutomationTasks", x => new { x.SiteId, x.AutomationTaskId });
                    table.ForeignKey(
                        name: "FK_SiteAutomationTasks_AutomationTasks_AutomationTaskId",
                        column: x => x.AutomationTaskId,
                        principalTable: "AutomationTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SiteAutomationTasks_Sites_SiteId_ClientId",
                        columns: x => new { x.SiteId, x.ClientId },
                        principalTable: "Sites",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutomationTaskRunTargets",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    DueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    OutcomeHandledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationTaskRunTargets", x => new { x.RunId, x.EndpointId });
                    table.ForeignKey(
                        name: "FK_AutomationTaskRunTargets_AutomationTaskRuns_RunId_ClientId",
                        columns: x => new { x.RunId, x.ClientId },
                        principalTable: "AutomationTaskRuns",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AutomationTaskRunTargets_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AutomationTaskRunTargets_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_TaskId_EndpointId_CreatedAt",
                table: "Jobs",
                columns: new[] { "TaskId", "EndpointId", "CreatedAt" },
                filter: "\"TaskId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_TaskRunId",
                table: "Jobs",
                column: "TaskRunId",
                filter: "\"TaskRunId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTaskRuns_AutomationTaskId_CreatedAt",
                table: "AutomationTaskRuns",
                columns: new[] { "AutomationTaskId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTaskRuns_ClientId",
                table: "AutomationTaskRuns",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTaskRuns_Occurrence",
                table: "AutomationTaskRuns",
                columns: new[] { "AutomationTaskId", "ClientId", "OccurrenceAt" },
                unique: true,
                filter: "\"Trigger\" = 'Schedule'");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTaskRunTargets_AutomationTaskId_EndpointId",
                table: "AutomationTaskRunTargets",
                columns: new[] { "AutomationTaskId", "EndpointId" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTaskRunTargets_EndpointId_ClientId",
                table: "AutomationTaskRunTargets",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTaskRunTargets_Outcome",
                table: "AutomationTaskRunTargets",
                column: "JobId",
                filter: "\"JobId\" IS NOT NULL AND \"OutcomeHandledAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTaskRunTargets_Pending",
                table: "AutomationTaskRunTargets",
                column: "DueAt",
                filter: "\"State\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTaskRunTargets_RunId_ClientId",
                table: "AutomationTaskRunTargets",
                columns: new[] { "RunId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTasks_ClientId_Name",
                table: "AutomationTasks",
                columns: new[] { "ClientId", "Name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTasks_ScriptId",
                table: "AutomationTasks",
                column: "ScriptId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientAutomationTasks_AutomationTaskId",
                table: "ClientAutomationTasks",
                column: "AutomationTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_EndpointAutomationTasks_AutomationTaskId",
                table: "EndpointAutomationTasks",
                column: "AutomationTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_EndpointAutomationTasks_EndpointId_ClientId",
                table: "EndpointAutomationTasks",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_SiteAutomationTasks_AutomationTaskId",
                table: "SiteAutomationTasks",
                column: "AutomationTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_SiteAutomationTasks_SiteId_ClientId",
                table: "SiteAutomationTasks",
                columns: new[] { "SiteId", "ClientId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_AutomationTaskRuns_TaskRunId",
                table: "Jobs",
                column: "TaskRunId",
                principalTable: "AutomationTaskRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_AutomationTasks_TaskId",
                table: "Jobs",
                column: "TaskId",
                principalTable: "AutomationTasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.Sql(ClientRulesSql);
            migrationBuilder.Sql(JobOriginSql);
            migrationBuilder.Sql(OriginFunctionSql);
            migrationBuilder.Sql(JobBindingSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PreviousJobBindingSql);
            migrationBuilder.Sql(AgentUninstalls.OriginFunctionSql);
            migrationBuilder.Sql(Action1AgentAutoInstall.JobOriginSql);
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_AutomationTasks_ScriptClient" ON "AutomationTasks";
                DROP FUNCTION IF EXISTS fleeto_task_script_client();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_AutomationTaskRuns_TaskRunId",
                table: "Jobs");

            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_AutomationTasks_TaskId",
                table: "Jobs");

            migrationBuilder.DropTable(
                name: "AutomationTaskRunTargets");

            migrationBuilder.DropTable(
                name: "AutomationTaskStates");

            migrationBuilder.DropTable(
                name: "ClientAutomationTasks");

            migrationBuilder.DropTable(
                name: "EndpointAutomationTasks");

            migrationBuilder.DropTable(
                name: "SiteAutomationTasks");

            migrationBuilder.DropTable(
                name: "AutomationTaskRuns");

            migrationBuilder.DropTable(
                name: "AutomationTasks");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_TaskId_EndpointId_CreatedAt",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_TaskRunId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "TaskId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "TaskRunId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ManagedSince",
                table: "Endpoints");
        }
    }
}
