using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <summary>
    /// The patch management of the instance (0.9.0, <c>PATCHING.md</c> §16): one setting, <c>patching.manager</c>. An instance that has
    /// an Action1 integration keeps working with Action1 as before; any other instance, a new one included, patches with Fleeto. A row
    /// that already exists is left alone. Data only, so the previous release runs on it; there is nothing to undo.
    /// </summary>
    public partial class PatchManager : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO "Settings" ("Key", "Value", "IsEncrypted", "UpdatedAt")
                SELECT 'patching.manager',
                       CASE WHEN EXISTS (SELECT 1 FROM "Integrations" WHERE "Type" = 'Action1') THEN 'Action1' ELSE 'Fleeto' END,
                       false, now()
                ON CONFLICT ("Key") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
