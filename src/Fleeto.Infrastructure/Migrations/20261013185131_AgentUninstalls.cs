using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <summary>
    /// Uninstalling the agent of a deleted endpoint (0.6.0): the waiting row with the certificate fingerprints and the signed command.
    /// Additive: the previous release never reads the table. Only web may ask for the signature of an uninstall command
    /// (TR_SigningRequests_Origin), and what web and the signer wrote cannot change afterwards (TR_AgentUninstalls_Binding): the gateway
    /// only records the answer.
    /// </summary>
    public partial class AgentUninstalls : Migration
    {
        /// <summary>The rule as of 0.6.0 with uninstall commands: every <c>SigningRequestKind</c> with the roles that may create it.</summary>
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

        /// <summary>What web asked and the signer signed stays as it is; the gateway may only record the answer.</summary>
        internal const string BindingSql = """
            CREATE OR REPLACE FUNCTION fleeto_agent_uninstall_binding() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."ClientId" IS DISTINCT FROM OLD."ClientId" OR NEW."EndpointId" IS DISTINCT FROM OLD."EndpointId"
                 OR NEW."Fingerprints" IS DISTINCT FROM OLD."Fingerprints" OR NEW."RequestedByUserId" IS DISTINCT FROM OLD."RequestedByUserId"
                 OR NEW."RequestedAt" IS DISTINCT FROM OLD."RequestedAt" OR NEW."ExpiresAt" IS DISTINCT FROM OLD."ExpiresAt"
                 OR (OLD."SignedAt" IS NOT NULL AND (NEW."Payload" IS DISTINCT FROM OLD."Payload" OR NEW."Signature" IS DISTINCT FROM OLD."Signature"
                     OR NEW."SigningKeyId" IS DISTINCT FROM OLD."SigningKeyId" OR NEW."SignedAt" IS DISTINCT FROM OLD."SignedAt"))
                 OR (current_user = 'fleeto_gateway' AND (NEW."Payload" IS DISTINCT FROM OLD."Payload" OR NEW."Signature" IS DISTINCT FROM OLD."Signature"
                     OR NEW."SignedAt" IS DISTINCT FROM OLD."SignedAt" OR NEW."RefusalReason" IS DISTINCT FROM OLD."RefusalReason")) THEN
                RAISE EXCEPTION 'An uninstall request cannot change after it was made or signed' USING ERRCODE = 'insufficient_privilege';
              END IF;
              RETURN NEW;
            END $$;
            DROP TRIGGER IF EXISTS "TR_AgentUninstalls_Binding" ON "AgentUninstalls";
            CREATE TRIGGER "TR_AgentUninstalls_Binding" BEFORE UPDATE ON "AgentUninstalls" FOR EACH ROW EXECUTE FUNCTION fleeto_agent_uninstall_binding();
            """;
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentUninstalls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    Hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Fingerprints = table.Column<List<string>>(type: "text[]", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: true),
                    Signature = table.Column<byte[]>(type: "bytea", nullable: true),
                    SigningKeyId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    SignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RefusalReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AcceptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcceptedBy = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentUninstalls", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentUninstalls_ExpiresAt",
                table: "AgentUninstalls",
                column: "ExpiresAt");

            migrationBuilder.Sql(OriginFunctionSql);
            migrationBuilder.Sql(BindingSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Action1AgentAutoInstall.OriginFunctionSql);
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_AgentUninstalls_Binding" ON "AgentUninstalls";
                DROP FUNCTION IF EXISTS fleeto_agent_uninstall_binding();
                """);
            migrationBuilder.DropTable(
                name: "AgentUninstalls");
        }
    }
}
