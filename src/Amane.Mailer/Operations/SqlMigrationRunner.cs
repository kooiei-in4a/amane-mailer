using Amane.Mailer.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Operations;

public sealed class SqlMigrationRunner(SqliteConnectionFactory connections)
{
    public async Task<IReadOnlyList<string>> ApplyPendingAsync(CancellationToken cancellationToken = default)
    {
        var migrationDirectory = Path.Combine(AppContext.BaseDirectory, "Data", "Migrations");
        if (!Directory.Exists(migrationDirectory))
        {
            throw new DirectoryNotFoundException($"Migration directory not found: {migrationDirectory}");
        }

        var files = Directory.GetFiles(migrationDirectory, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await EnsureSchemaMigrationsTableAsync(connection, cancellationToken);

        var applied = new List<string>();
        foreach (var file in files)
        {
            var version = Path.GetFileName(file);
            if (await IsAppliedAsync(connection, version, cancellationToken))
            {
                continue;
            }

            var sql = await File.ReadAllTextAsync(file, cancellationToken);
            await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
            try
            {
                await using (var script = connection.CreateCommand())
                {
                    script.CommandText = sql;
                    await script.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var record = connection.CreateCommand())
                {
                    record.CommandText = """
                        INSERT INTO schema_migrations (version, applied_at)
                        VALUES (@Version, @AppliedAt);
                        """;
                    record.Parameters.AddWithValue("@Version", version);
                    record.Parameters.AddWithValue("@AppliedAt", SqliteTime.ToStorageUtc(SqliteTime.UtcNow));
                    await record.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                applied.Add(version);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        return applied;
    }

    private static async Task EnsureSchemaMigrationsTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version     TEXT NOT NULL PRIMARY KEY,
                applied_at  TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> IsAppliedAsync(
        SqliteConnection connection,
        string version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM schema_migrations WHERE version = @Version LIMIT 1;";
        command.Parameters.AddWithValue("@Version", version);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }
}
