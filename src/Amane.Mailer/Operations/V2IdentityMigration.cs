using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Operations;

internal static class V2IdentityMigration
{
    public const string MigrationVersion = "019_sender_api_key_identity.sql";

    public static readonly SqlMigrationRunner.MigrationTransactionStep Step = new(
        ValidatePreconditionBeforeScriptAsync,
        static (_, _) => Task.CompletedTask);

    private static async Task ValidatePreconditionBeforeScriptAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                EXISTS (SELECT 1 FROM mail_requests LIMIT 1)
                OR EXISTS (SELECT 1 FROM delivery_events LIMIT 1)
                OR EXISTS (SELECT 1 FROM mail_suppressions LIMIT 1)
                OR EXISTS (SELECT 1 FROM provider_event_inbox LIMIT 1);
            """;
        var populated = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0;
        if (populated)
        {
            throw new InvalidOperationException(
                "Unsupported major upgrade: populated v1 mail state cannot be interpreted as v2 Sender state. Start a fresh v2 database.");
        }
    }
}
