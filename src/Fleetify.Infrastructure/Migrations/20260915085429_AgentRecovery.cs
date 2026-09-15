using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleetify.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AgentRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EndpointId",
                table: "EnrollmentTokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EnrollmentTokens_EndpointId_ClientId",
                table: "EnrollmentTokens",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.AddForeignKey(
                name: "FK_EnrollmentTokens_Endpoints_EndpointId_ClientId",
                table: "EnrollmentTokens",
                columns: new[] { "EndpointId", "ClientId" },
                principalTable: "Endpoints",
                principalColumns: new[] { "Id", "ClientId" },
                onDelete: ReferentialAction.Cascade);

            // Recovery requests may only come from the gateway, like enrollment and renewal; a kind the function does not know
            // is refused for every container role from now on, so a future kind cannot slip through unrestricted.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION fleetify_signing_request_origin() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."Kind" IN ('AgentEnrollment', 'AgentRenewal', 'GatewayCertificate', 'AgentRecovery') THEN
                    IF current_user IN ('fleetify_web', 'fleetify_workers', 'fleetify_signer') THEN
                      RAISE EXCEPTION '% requests may only be created by the gateway', NEW."Kind" USING ERRCODE = 'insufficient_privilege';
                    END IF;
                  ELSIF NEW."Kind" = 'AgentConfig' THEN
                    IF current_user IN ('fleetify_web', 'fleetify_gateway') THEN
                      RAISE EXCEPTION 'AgentConfig requests may only be created by the workers or the signer' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                  ELSIF current_user IN ('fleetify_web', 'fleetify_workers', 'fleetify_signer', 'fleetify_gateway') THEN
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
                CREATE OR REPLACE FUNCTION fleetify_signing_request_origin() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."Kind" IN ('AgentEnrollment', 'AgentRenewal', 'GatewayCertificate') THEN
                    IF current_user IN ('fleetify_web', 'fleetify_workers', 'fleetify_signer') THEN
                      RAISE EXCEPTION '% requests may only be created by the gateway', NEW."Kind" USING ERRCODE = 'insufficient_privilege';
                    END IF;
                  ELSIF NEW."Kind" = 'AgentConfig' THEN
                    IF current_user IN ('fleetify_web', 'fleetify_gateway') THEN
                      RAISE EXCEPTION 'AgentConfig requests may only be created by the workers or the signer' USING ERRCODE = 'insufficient_privilege';
                    END IF;
                  END IF;
                  RETURN NEW;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_EnrollmentTokens_Endpoints_EndpointId_ClientId",
                table: "EnrollmentTokens");

            migrationBuilder.DropIndex(
                name: "IX_EnrollmentTokens_EndpointId_ClientId",
                table: "EnrollmentTokens");

            migrationBuilder.DropColumn(
                name: "EndpointId",
                table: "EnrollmentTokens");
        }
    }
}
