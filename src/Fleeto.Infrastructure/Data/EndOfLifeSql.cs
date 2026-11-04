namespace Fleeto.Infrastructure.Data;

/// <summary>SQL of the migration of the end-of-life report (0.8.0) that EF Core does not express.</summary>
public static class EndOfLifeSql
{
    /// <summary>A notification on <c>fleeto_end_of_life</c> when a user asks for a new report, so the workers run it at once.</summary>
    public const string RefreshUp = """
        CREATE OR REPLACE FUNCTION fleeto_notify_end_of_life() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          PERFORM pg_notify('fleeto_end_of_life', NEW."Id"::text);
          RETURN NULL;
        END $$;
        CREATE TRIGGER "TR_EndOfLifeRefreshRequests_Notify" AFTER INSERT ON "EndOfLifeRefreshRequests" FOR EACH ROW
          EXECUTE FUNCTION fleeto_notify_end_of_life();
        """;

    public const string RefreshDown = """
        DROP TRIGGER IF EXISTS "TR_EndOfLifeRefreshRequests_Notify" ON "EndOfLifeRefreshRequests";
        DROP FUNCTION IF EXISTS fleeto_notify_end_of_life();
        """;
}
