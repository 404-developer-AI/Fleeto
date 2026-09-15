using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <summary>
    /// Restricts which database role may request which kind of signature, so a compromised web or workers container
    /// cannot obtain a gateway server certificate (and impersonate the gateway to agents) or an agent certificate.
    /// Superusers and the migrator are not restricted (tests, maintenance).
    /// </summary>
    [DbContext(typeof(FleetoDbContext))]
    [Migration("20260914140000_SigningRequestOrigin")]
    public partial class SigningRequestOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION fleeto_signing_request_origin() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."Kind" IN ('AgentEnrollment', 'AgentRenewal', 'GatewayCertificate') THEN
                    IF current_user IN ('fleeto_web', 'fleeto_workers', 'fleeto_signer') THEN
                      RAISE EXCEPTION '% requests may only be created by the gateway', NEW."Kind" USING ERRCODE = 'insufficient_privilege';
                    END IF;
                  ELSIF NEW."Kind" = 'AgentConfig' THEN
                    IF current_user IN ('fleeto_web', 'fleeto_gateway') THEN
                      RAISE EXCEPTION 'AgentConfig requests may only be created by the workers or the signer' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER "TR_SigningRequests_Origin" BEFORE INSERT ON "SigningRequests"
                  FOR EACH ROW EXECUTE FUNCTION fleeto_signing_request_origin();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_SigningRequests_Origin" ON "SigningRequests";
                DROP FUNCTION IF EXISTS fleeto_signing_request_origin();
                """);
        }
    }
}
