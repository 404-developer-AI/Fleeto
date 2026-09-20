using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <summary>
    /// What the signer signs can no longer be changed after it was requested (security review of 0.3.0 step 7). The gateway updates jobs,
    /// remote sessions and their participants (delivery, state, the join and the end) and the endpoints (online state, inventory); before
    /// this, a compromised gateway could change the row the signer reads between the request and the signature: point a job or a remote
    /// session at another endpoint, put its own key in a remote session token, or switch an endpoint to managed. These triggers keep the
    /// binding columns as they were inserted, for every role, and let a state never go back to waiting for a signature. The gateway may no
    /// longer change the tier, client, site or class of an endpoint.
    /// </summary>
    public partial class ImmutableSigningBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
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
                CREATE TRIGGER "TR_Jobs_Binding" BEFORE UPDATE ON "Jobs" FOR EACH ROW EXECUTE FUNCTION fleeto_job_binding();

                CREATE OR REPLACE FUNCTION fleeto_remote_session_binding() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF (NEW."Id", NEW."ClientId", NEW."EndpointId", NEW."Kind", NEW."Component", NEW."WindowsSessionId", NEW."StartedByUserId",
                      NEW."CreatedAt")
                     IS DISTINCT FROM
                     (OLD."Id", OLD."ClientId", OLD."EndpointId", OLD."Kind", OLD."Component", OLD."WindowsSessionId", OLD."StartedByUserId",
                      OLD."CreatedAt") THEN
                    RAISE EXCEPTION 'remote session %: what was requested cannot be changed', OLD."Id" USING ERRCODE = 'insufficient_privilege';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER "TR_RemoteSessions_Binding" BEFORE UPDATE ON "RemoteSessions"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_remote_session_binding();

                CREATE OR REPLACE FUNCTION fleeto_remote_participant_binding() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF (NEW."Id", NEW."SessionId", NEW."ClientId", NEW."EndpointId", NEW."UserId", NEW."BrowserPublicKey", NEW."CreatedAt")
                     IS DISTINCT FROM
                     (OLD."Id", OLD."SessionId", OLD."ClientId", OLD."EndpointId", OLD."UserId", OLD."BrowserPublicKey", OLD."CreatedAt") THEN
                    RAISE EXCEPTION 'remote session participant %: what was requested cannot be changed', OLD."Id" USING ERRCODE = 'insufficient_privilege';
                  END IF;
                  IF NEW."State" = 'Requested' AND OLD."State" <> 'Requested' THEN
                    RAISE EXCEPTION 'remote session participant %: a participant never waits for a signature again', OLD."Id"
                      USING ERRCODE = 'insufficient_privilege';
                  END IF;
                  IF (OLD."TokenPayload" IS NOT NULL AND NEW."TokenPayload" IS DISTINCT FROM OLD."TokenPayload")
                     OR (OLD."TokenSignature" IS NOT NULL AND NEW."TokenSignature" IS DISTINCT FROM OLD."TokenSignature") THEN
                    RAISE EXCEPTION 'remote session participant %: a signed token cannot be changed', OLD."Id" USING ERRCODE = 'insufficient_privilege';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER "TR_RemoteSessionParticipants_Binding" BEFORE UPDATE ON "RemoteSessionParticipants"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_remote_participant_binding();

                CREATE OR REPLACE FUNCTION fleeto_endpoint_gateway_columns() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF current_user = 'fleeto_gateway' AND (NEW."Tier", NEW."ClientId", NEW."SiteId", NEW."ClassOverride")
                     IS DISTINCT FROM (OLD."Tier", OLD."ClientId", OLD."SiteId", OLD."ClassOverride") THEN
                    RAISE EXCEPTION 'endpoint %: the gateway cannot change its tier, client, site or class', OLD."Id"
                      USING ERRCODE = 'insufficient_privilege';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER "TR_Endpoints_GatewayColumns" BEFORE UPDATE ON "Endpoints"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_endpoint_gateway_columns();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_Endpoints_GatewayColumns" ON "Endpoints";
                DROP FUNCTION IF EXISTS fleeto_endpoint_gateway_columns();
                DROP TRIGGER IF EXISTS "TR_RemoteSessionParticipants_Binding" ON "RemoteSessionParticipants";
                DROP FUNCTION IF EXISTS fleeto_remote_participant_binding();
                DROP TRIGGER IF EXISTS "TR_RemoteSessions_Binding" ON "RemoteSessions";
                DROP FUNCTION IF EXISTS fleeto_remote_session_binding();
                DROP TRIGGER IF EXISTS "TR_Jobs_Binding" ON "Jobs";
                DROP FUNCTION IF EXISTS fleeto_job_binding();
                """);
        }
    }
}
