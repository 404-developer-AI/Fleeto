using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <summary>
    /// The internal rename from Fleetify to Fleeto (0.2.1), for databases created before it. Earlier migrations now create the Fleeto
    /// names directly, so on a new database this changes nothing. install.sh (or setup-dev.ps1) has already renamed the database
    /// and its login roles, which needs a superuser; this migration renames what the migrator owns:
    /// <list type="bullet">
    /// <item>functions named fleetify_*, and function bodies that name the old roles or notification channels;</item>
    /// <item>triggers that call those functions or pass an old channel name, recreated from their own definition;</item>
    /// <item>every endpoint configuration is signed again, because the agent now verifies the fleeto-agent-config-v1 context.</item>
    /// </list>
    /// </summary>
    public partial class RenameToFleeto : Migration
    {
        private const string LegacyPrefix = "fleetify_";
        private const string CurrentPrefix = "fleeto_";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RenameSql(LegacyPrefix, CurrentPrefix));

            migrationBuilder.Sql(ResignConfigurationsSql);
        }

        /// <summary>
        /// Configurations signed with the old context are refused by agents of this release: forget the stored content hash, so the
        /// signer signs every configuration again, and let the workers fan the change out to every endpoint.
        /// </summary>
        internal const string ResignConfigurationsSql = """
            UPDATE "EndpointConfigs" SET "ContentHash" = '';
            INSERT INTO "ConfigChangeEvents" ("Scope", "ScopeId", "CreatedAt")
            SELECT 'Instance', NULL, now()
            WHERE EXISTS (SELECT 1 FROM "EndpointConfigs");
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RenameSql(CurrentPrefix, LegacyPrefix));
        }

        /// <summary>
        /// Renames functions and the triggers using them from one prefix to the other, generically from the catalog, so every function
        /// and trigger of every earlier migration is covered. Idempotent: nothing matches once it ran.
        /// </summary>
        internal static string RenameSql(string from, string to) => $$"""
            DO $rename$
            DECLARE
              r record;
            BEGIN
              -- 1. Functions named with the old prefix or mentioning it (role names, channel names): create the renamed version.
              --    A function that already exists under the new name was written by a later migration and is newer: keep it.
              FOR r IN
                SELECT p.oid, p.proname
                FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname = 'public' AND p.prokind = 'f'
                  AND (p.proname LIKE '{{from}}%' OR p.prosrc LIKE '%{{from}}%')
              LOOP
                CONTINUE WHEN starts_with(r.proname, '{{from}}') AND EXISTS (
                  SELECT 1 FROM pg_proc q JOIN pg_namespace m ON m.oid = q.pronamespace
                  WHERE m.nspname = 'public' AND q.prokind = 'f' AND q.proname = '{{to}}' || substr(r.proname, length('{{from}}') + 1));
                EXECUTE replace(pg_get_functiondef(r.oid), '{{from}}', '{{to}}');
              END LOOP;

              -- 2. Triggers calling an old function or passing an old name as argument: recreate them on the new function.
              FOR r IN
                SELECT t.tgname, c.relname, pg_get_triggerdef(t.oid) AS definition
                FROM pg_trigger t
                  JOIN pg_class c ON c.oid = t.tgrelid
                  JOIN pg_namespace n ON n.oid = c.relnamespace
                  JOIN pg_proc p ON p.oid = t.tgfoid
                WHERE n.nspname = 'public' AND NOT t.tgisinternal
                  AND (p.proname LIKE '{{from}}%' OR pg_get_triggerdef(t.oid) LIKE '%{{from}}%')
              LOOP
                EXECUTE format('DROP TRIGGER %I ON public.%I', r.tgname, r.relname);
                EXECUTE replace(r.definition, '{{from}}', '{{to}}');
              END LOOP;

              -- 3. The old functions are no longer used.
              FOR r IN
                SELECT p.oid::regprocedure AS signature
                FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname = 'public' AND p.prokind = 'f' AND p.proname LIKE '{{from}}%'
              LOOP
                EXECUTE format('DROP FUNCTION %s', r.signature);
              END LOOP;
            END
            $rename$;
            """;
    }
}
