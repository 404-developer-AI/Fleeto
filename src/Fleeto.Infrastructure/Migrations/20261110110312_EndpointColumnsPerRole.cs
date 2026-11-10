using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <summary>
    /// The columns of an endpoint the gateway and the workers may change (security review of 0.9.0). Before this the gateway was refused
    /// only the tier, client, site and class override, so a compromised gateway could still write ManagedSince, which starts the tasks
    /// "when an endpoint becomes managed" again, and every other column it has no business with (maintenance, description, the config
    /// version, the refresh request). The gateway may now change only what it writes itself: the online state of the agent and the watchdog,
    /// the facts the agent reports (host name, operating system, class it detected, agent version, public address, signed-in users) and the
    /// config version the agent applied. Every other column, also one a later migration adds, is refused until a migration allows it
    /// (SigningBindingTests enumerates them). The workers never change the tier: only the tier service in web does, so they are refused
    /// Tier and ManagedSince. Expand/contract: the previous release writes nothing the new rule refuses.
    /// </summary>
    public partial class EndpointColumnsPerRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION fleeto_endpoint_gateway_columns() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                  gateway_columns CONSTANT text[] := ARRAY[
                    'AgentVersion', 'AppliedConfigVersion', 'Architecture', 'DetectedClass', 'Hostname', 'IsOnline', 'LastSeenAt',
                    'OsName', 'OsPlatform', 'OsVersion', 'PublicIpAddress', 'PublicIpSeenAt', 'SignedInUsersAt', 'SignedInUsersJson',
                    'UpdatedAt', 'WatchdogLastSeenAt', 'WatchdogOnline', 'WatchdogVersion'];
                BEGIN
                  IF current_user = 'fleeto_gateway' THEN
                    IF (to_jsonb(NEW) - gateway_columns) IS DISTINCT FROM (to_jsonb(OLD) - gateway_columns) THEN
                      RAISE EXCEPTION 'endpoint %: the gateway can change only its online state and what the agent reports', OLD."Id"
                        USING ERRCODE = 'insufficient_privilege';
                    END IF;
                  ELSIF current_user = 'fleeto_workers' THEN
                    IF (NEW."Tier", NEW."ManagedSince") IS DISTINCT FROM (OLD."Tier", OLD."ManagedSince") THEN
                      RAISE EXCEPTION 'endpoint %: the workers cannot change its tier', OLD."Id"
                        USING ERRCODE = 'insufficient_privilege';
                    END IF;
                  END IF;
                  RETURN NEW;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION fleeto_endpoint_gateway_columns() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF current_user = 'fleeto_gateway' AND (NEW."Tier", NEW."ClientId", NEW."SiteId", NEW."ClassOverride")
                     IS DISTINCT FROM (OLD."Tier", OLD."ClientId", OLD."SiteId", OLD."ClassOverride") THEN
                    RAISE EXCEPTION 'endpoint %: the gateway cannot change its tier, client, site or class', OLD."Id"
                      USING ERRCODE = 'insufficient_privilege';
                  END IF;
                  RETURN NEW;
                END $$;
                """);
        }
    }
}
