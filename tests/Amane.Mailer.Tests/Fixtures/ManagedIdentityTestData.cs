using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Tests.Fixtures;

internal static class ManagedIdentityTestData
{
    public static async Task SeedAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO senders (
                sender_id, email, display_name, enabled, created_at, disabled_at)
            VALUES (
                @SenderId, 'noreply@example.com', 'Example Service', 1,
                '2026-01-01T00:00:00.0000000Z', NULL);
            INSERT OR IGNORE INTO api_keys (
                key_id, sender_id, name, secret_digest, created_at, revoked_at)
            VALUES (
                @SenderId, @SenderId, 'test',
                X'66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925',
                '2026-01-01T00:00:00.0000000Z', NULL);
            """;
        command.Parameters.AddWithValue(
            "@SenderId",
            MailerWebApplicationFixtureBase.TenantId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
