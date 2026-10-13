using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <summary>
    /// Automatic installation of the Action1 agent (0.6.0): the switch on the patch policy and the state per endpoint. Additive:
    /// the previous release keeps running, because the new column has a database default and nobody but the new workers
    /// writes the new table. From here the workers may write one kind of job, the installation of the Action1 agent without
    /// a user, and ask for its signature (TR_Jobs_Origin, TR_SigningRequests_Origin); web may no longer write a job without
    /// a user.
    /// </summary>
    public partial class Action1AgentAutoInstall : Migration
    {
        /// <summary>The rule as of 0.6.0: every <c>SigningRequestKind</c> with the roles that may create it.</summary>
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
                      SELECT 1 FROM "Jobs" j WHERE j."Id" = NEW."SubjectId" AND j."ClientId" = NEW."ClientId" AND j."Type" = 'Action1Agent'
                        AND j."InitiatedByUserId" = '00000000-0000-0000-0000-000000000000' AND j."State" = 'PendingSignature') THEN
                    RAISE EXCEPTION 'The workers may only request the signature of an automatic installation of the Action1 agent'
                      USING ERRCODE = 'insufficient_privilege';
                  END IF;
                END IF;
              ELSIF NEW."Kind" = 'RemoteSessionToken' THEN
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
        /// Who may write which job (0.6.0): a job without a user is only the automatic installation of the Action1 agent, and
        /// only the workers write it; the workers write no other job.
        /// </summary>
        internal const string JobOriginSql = """
            CREATE OR REPLACE FUNCTION fleeto_job_origin() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF current_user = 'fleeto_workers' AND NOT (NEW."Type" = 'Action1Agent'
                  AND NEW."InitiatedByUserId" = '00000000-0000-0000-0000-000000000000') THEN
                RAISE EXCEPTION 'The workers may only write an automatic installation of the Action1 agent' USING ERRCODE = 'insufficient_privilege';
              ELSIF current_user IN ('fleeto_web', 'fleeto_gateway', 'fleeto_signer')
                  AND NEW."InitiatedByUserId" = '00000000-0000-0000-0000-000000000000' THEN
                RAISE EXCEPTION 'A job without a user may only be written by the workers' USING ERRCODE = 'insufficient_privilege';
              END IF;
              RETURN NEW;
            END $$;
            DROP TRIGGER IF EXISTS "TR_Jobs_Origin" ON "Jobs";
            CREATE TRIGGER "TR_Jobs_Origin" BEFORE INSERT ON "Jobs" FOR EACH ROW EXECUTE FUNCTION fleeto_job_origin();
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoInstallAgent",
                table: "PatchPolicies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Action1AgentInstallStates",
                columns: table => new
                {
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    JobSucceededAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AgentReportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Failure = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RemovalId = table.Column<Guid>(type: "uuid", nullable: true),
                    CheckWrittenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CheckDetail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Action1AgentInstallStates", x => x.EndpointId);
                    table.ForeignKey(
                        name: "FK_Action1AgentInstallStates_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Action1AgentInstallStates_ClientId_Status",
                table: "Action1AgentInstallStates",
                columns: new[] { "ClientId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Action1AgentInstallStates_EndpointId_ClientId",
                table: "Action1AgentInstallStates",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_Action1AgentInstallStates_NextAttemptAt",
                table: "Action1AgentInstallStates",
                column: "NextAttemptAt",
                filter: "\"NextAttemptAt\" IS NOT NULL");

            migrationBuilder.Sql(OriginFunctionSql);
            migrationBuilder.Sql(JobOriginSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_Jobs_Origin" ON "Jobs";
                DROP FUNCTION IF EXISTS fleeto_job_origin();
                """);
            migrationBuilder.Sql(RemoteSessions.OriginFunctionSql);

            migrationBuilder.DropTable(
                name: "Action1AgentInstallStates");

            migrationBuilder.DropColumn(
                name: "AutoInstallAgent",
                table: "PatchPolicies");
        }
    }
}
