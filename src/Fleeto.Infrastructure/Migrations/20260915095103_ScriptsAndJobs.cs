using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScriptsAndJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ScriptApprovalRequired",
                table: "Policies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Scripts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CurrentVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Scripts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Scripts_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScriptVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScriptId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: true),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Body = table.Column<string>(type: "character varying(262144)", maxLength: 262144, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScriptVersions", x => x.Id);
                    table.CheckConstraint("CK_ScriptVersions_Approval", "(\"ApprovedAt\" IS NULL) = (\"ApprovedByUserId\" IS NULL) AND (\"ApprovedByUserId\" IS NULL OR \"ApprovedByUserId\" <> \"AuthorUserId\")");
                    table.ForeignKey(
                        name: "FK_ScriptVersions_Scripts_ScriptId",
                        column: x => x.ScriptId,
                        principalTable: "Scripts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScriptId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScriptVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScriptName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ScriptVersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScriptSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxOutputBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InitiatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    InitiatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RefusalReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: true),
                    Signature = table.Column<byte[]>(type: "bytea", nullable: true),
                    SigningKeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Result = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ExitCode = table.Column<int>(type: "integer", nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    OutputState = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReceivedOutputBytes = table.Column<long>(type: "bigint", nullable: false),
                    OutputTruncated = table.Column<bool>(type: "boolean", nullable: false),
                    StdoutChunks = table.Column<long>(type: "bigint", nullable: true),
                    StdoutBytes = table.Column<long>(type: "bigint", nullable: true),
                    StdoutSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    StderrChunks = table.Column<long>(type: "bigint", nullable: true),
                    StderrBytes = table.Column<long>(type: "bigint", nullable: true),
                    StderrSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                    table.UniqueConstraint("AK_Jobs_Id_ClientId", x => new { x.Id, x.ClientId });
                    table.CheckConstraint("CK_Jobs_Signed", "\"State\" IN ('PendingSignature', 'Refused', 'Cancelled', 'Expired') OR \"Signature\" IS NOT NULL");
                    table.CheckConstraint("CK_Jobs_Validity", "\"ValidUntil\" > \"CreatedAt\" AND \"ValidUntil\" <= \"CreatedAt\" + interval '7 days 5 minutes'");
                    table.ForeignKey(
                        name: "FK_Jobs_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Jobs_ScriptVersions_ScriptVersionId",
                        column: x => x.ScriptVersionId,
                        principalTable: "ScriptVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Jobs_Scripts_ScriptId",
                        column: x => x.ScriptId,
                        principalTable: "Scripts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "JobOutputChunks",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stream = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobOutputChunks", x => new { x.JobId, x.Stream, x.Sequence });
                    table.CheckConstraint("CK_JobOutputChunks_Size", "octet_length(\"Data\") BETWEEN 1 AND 65536");
                    table.ForeignKey(
                        name: "FK_JobOutputChunks_Jobs_JobId_ClientId",
                        columns: x => new { x.JobId, x.ClientId },
                        principalTable: "Jobs",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobOutputChunks_JobId_ClientId",
                table: "JobOutputChunks",
                columns: new[] { "JobId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_JobOutputChunks_ReceivedAt",
                table: "JobOutputChunks",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_BatchId",
                table: "Jobs",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_EndpointId_ClientId",
                table: "Jobs",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_EndpointId_CreatedAt",
                table: "Jobs",
                columns: new[] { "EndpointId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ScriptId",
                table: "Jobs",
                column: "ScriptId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ScriptVersionId",
                table: "Jobs",
                column: "ScriptVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_State_EndpointId",
                table: "Jobs",
                columns: new[] { "State", "EndpointId" },
                filter: "\"State\" IN ('PendingSignature', 'Queued', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_Scripts_ClientId_Name",
                table: "Scripts",
                columns: new[] { "ClientId", "Name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_ScriptVersions_ScriptId_Number",
                table: "ScriptVersions",
                columns: new[] { "ScriptId", "Number" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_Scripts_ClientIdImmutable" BEFORE UPDATE OF "ClientId" ON "Scripts" FOR EACH ROW EXECUTE FUNCTION fleeto_client_id_immutable();

                -- A script version carries exactly the ClientId of its script, null included.
                CREATE OR REPLACE FUNCTION fleeto_script_version_client() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE parent_client uuid;
                BEGIN
                  SELECT "ClientId" INTO parent_client FROM "Scripts" WHERE "Id" = NEW."ScriptId";
                  IF NEW."ClientId" IS DISTINCT FROM parent_client THEN
                    RAISE EXCEPTION 'ScriptVersions.ClientId must equal the ClientId of its script' USING ERRCODE = 'integrity_constraint_violation';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE CONSTRAINT TRIGGER "TR_ScriptVersions_Client" AFTER INSERT OR UPDATE ON "ScriptVersions"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_script_version_client();

                -- A job runs a global script or one of its own client; never another client's.
                CREATE OR REPLACE FUNCTION fleeto_job_script_client() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE script_client uuid;
                BEGIN
                  IF NEW."ScriptVersionId" IS NOT NULL THEN
                    SELECT "ClientId" INTO script_client FROM "ScriptVersions" WHERE "Id" = NEW."ScriptVersionId";
                    IF script_client IS NOT NULL AND script_client <> NEW."ClientId" THEN
                      RAISE EXCEPTION 'A job cannot run a script of another client' USING ERRCODE = 'integrity_constraint_violation';
                    END IF;
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE CONSTRAINT TRIGGER "TR_Jobs_ScriptClient" AFTER INSERT OR UPDATE OF "ScriptVersionId" ON "Jobs"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_job_script_client();

                -- Job signatures may only be requested by web.
                CREATE OR REPLACE FUNCTION fleeto_signing_request_origin() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."Kind" IN ('AgentEnrollment', 'AgentRenewal', 'GatewayCertificate', 'AgentRecovery') THEN
                    IF current_user IN ('fleeto_web', 'fleeto_workers', 'fleeto_signer') THEN
                      RAISE EXCEPTION '% requests may only be created by the gateway', NEW."Kind" USING ERRCODE = 'insufficient_privilege';
                    END IF;
                  ELSIF NEW."Kind" = 'AgentConfig' THEN
                    IF current_user IN ('fleeto_web', 'fleeto_gateway') THEN
                      RAISE EXCEPTION 'AgentConfig requests may only be created by the workers or the signer' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                  ELSIF NEW."Kind" = 'Job' THEN
                    IF current_user IN ('fleeto_gateway', 'fleeto_workers', 'fleeto_signer') THEN
                      RAISE EXCEPTION 'Job requests may only be created by web' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                  ELSIF current_user IN ('fleeto_web', 'fleeto_workers', 'fleeto_signer', 'fleeto_gateway') THEN
                    RAISE EXCEPTION 'Unknown signing request kind %', NEW."Kind" USING ERRCODE = 'insufficient_privilege';
                  END IF;
                  RETURN NEW;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION fleeto_signing_request_origin() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."Kind" IN ('AgentEnrollment', 'AgentRenewal', 'GatewayCertificate', 'AgentRecovery') THEN
                    IF current_user IN ('fleeto_web', 'fleeto_workers', 'fleeto_signer') THEN
                      RAISE EXCEPTION '% requests may only be created by the gateway', NEW."Kind" USING ERRCODE = 'insufficient_privilege';
                    END IF;
                  ELSIF NEW."Kind" = 'AgentConfig' THEN
                    IF current_user IN ('fleeto_web', 'fleeto_gateway') THEN
                      RAISE EXCEPTION 'AgentConfig requests may only be created by the workers or the signer' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                  ELSIF current_user IN ('fleeto_web', 'fleeto_workers', 'fleeto_signer', 'fleeto_gateway') THEN
                    RAISE EXCEPTION 'Unknown signing request kind %', NEW."Kind" USING ERRCODE = 'insufficient_privilege';
                  END IF;
                  RETURN NEW;
                END $$;
                DROP TRIGGER IF EXISTS "TR_Jobs_ScriptClient" ON "Jobs";
                DROP FUNCTION IF EXISTS fleeto_job_script_client();
                DROP TRIGGER IF EXISTS "TR_ScriptVersions_Client" ON "ScriptVersions";
                DROP FUNCTION IF EXISTS fleeto_script_version_client();
                DROP TRIGGER IF EXISTS "TR_Scripts_ClientIdImmutable" ON "Scripts";
                """);

            migrationBuilder.DropTable(
                name: "JobOutputChunks");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "ScriptVersions");

            migrationBuilder.DropTable(
                name: "Scripts");

            migrationBuilder.DropColumn(
                name: "ScriptApprovalRequired",
                table: "Policies");
        }
    }
}
