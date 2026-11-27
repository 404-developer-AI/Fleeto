using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <summary>
    /// Security advisories (0.9.0): the workers took the date of a feed item every time its source changed it, and the CCB moves the
    /// date when it edits an item, so advisories showed a publication after their handling. An advisory is no later than when Fleeto
    /// first stored it, so a later date goes back to that moment; the workers now keep the earliest date. Data only, so the previous
    /// release runs on it; there is nothing to undo.
    /// </summary>
    public partial class AdvisoryEarliestPublishedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE "Advisories" SET "PublishedAt" = "FetchedAt" WHERE "PublishedAt" > "FetchedAt";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
