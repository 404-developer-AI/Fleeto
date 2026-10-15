using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <summary>
    /// What the exit codes of a script mean (0.6.0, decided 2026-10-15): four lists on the script, read by the gateway when a job ends
    /// (<c>Jobs.ExitStatus</c>) and by the workers for a script check. Expand only: the lists default to empty in the database, and the
    /// exit codes a task step counted as success (alpha.23) move onto the script of the step as success, while the step keeps them
    /// for the build before this one.
    /// </summary>
    public partial class ScriptExitCodes : Migration
    {
        /// <summary>The codes steps counted as success become success codes of their script, at most 100 per script.</summary>
        internal const string MoveStepCodesSql = """
            UPDATE "Scripts" s SET "SuccessExitCodes" = c.codes
            FROM (SELECT st."ScriptId", (array_agg(DISTINCT code ORDER BY code))[1:100] AS codes
                  FROM "AutomationTaskSteps" st, unnest(st."SuccessExitCodes") AS code
                  WHERE st."ScriptId" IS NOT NULL
                  GROUP BY st."ScriptId") c
            WHERE s."Id" = c."ScriptId";
            """;

        /// <summary>
        /// What a job asked stays as it is; its script, script version, task, run and step may only become empty, when those are
        /// deleted (the foreign keys of the script and its version set null: until now the trigger refused that, so a script that
        /// had jobs could not be deleted). Its exit status is written once, with the result.
        /// </summary>
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

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<int>>(
                name: "ErrorExitCodes",
                table: "Scripts",
                type: "integer[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<List<int>>(
                name: "InfoExitCodes",
                table: "Scripts",
                type: "integer[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<List<int>>(
                name: "SuccessExitCodes",
                table: "Scripts",
                type: "integer[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<List<int>>(
                name: "WarningExitCodes",
                table: "Scripts",
                type: "integer[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string>(
                name: "ExitStatus",
                table: "Jobs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasWarning",
                table: "AutomationTaskRunTargets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Scripts_ExitCodes",
                table: "Scripts",
                sql: "NOT (\"SuccessExitCodes\" && \"InfoExitCodes\") AND NOT (\"SuccessExitCodes\" && \"WarningExitCodes\") AND NOT (\"SuccessExitCodes\" && \"ErrorExitCodes\") AND NOT (\"InfoExitCodes\" && \"WarningExitCodes\") AND NOT (\"InfoExitCodes\" && \"ErrorExitCodes\") AND NOT (\"WarningExitCodes\" && \"ErrorExitCodes\") AND cardinality(\"SuccessExitCodes\") + cardinality(\"InfoExitCodes\") + cardinality(\"WarningExitCodes\") + cardinality(\"ErrorExitCodes\") <= 100");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Jobs_ExitStatus",
                table: "Jobs",
                sql: "\"ExitStatus\" IS NULL OR (\"CompletedAt\" IS NOT NULL AND \"Result\" = 'Exited')");

            migrationBuilder.Sql(MoveStepCodesSql);
            migrationBuilder.Sql(JobBindingSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(TaskSteps.JobBindingSql);

            migrationBuilder.DropCheckConstraint(
                name: "CK_Scripts_ExitCodes",
                table: "Scripts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Jobs_ExitStatus",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ErrorExitCodes",
                table: "Scripts");

            migrationBuilder.DropColumn(
                name: "InfoExitCodes",
                table: "Scripts");

            migrationBuilder.DropColumn(
                name: "SuccessExitCodes",
                table: "Scripts");

            migrationBuilder.DropColumn(
                name: "WarningExitCodes",
                table: "Scripts");

            migrationBuilder.DropColumn(
                name: "ExitStatus",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "HasWarning",
                table: "AutomationTaskRunTargets");
        }
    }
}
