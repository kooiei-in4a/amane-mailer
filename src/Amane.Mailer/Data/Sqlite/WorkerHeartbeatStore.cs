using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite.Models;
using System.Text;

namespace Amane.Mailer.Data.Sqlite;

public sealed class WorkerHeartbeatStore(SqliteConnectionFactory connections)
{
    public async Task UpsertHeartbeatAsync(
        string name,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO worker_heartbeats (name, last_heartbeat_at)
            VALUES (@Name, @Now)
            ON CONFLICT(name) DO UPDATE SET last_heartbeat_at = excluded.last_heartbeat_at;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@Name", name);
        command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkerHeartbeat>> GetHeartbeatsAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT name, last_heartbeat_at FROM worker_heartbeats;";

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var heartbeats = new List<WorkerHeartbeat>();
        while (await reader.ReadAsync(cancellationToken))
        {
            heartbeats.Add(new WorkerHeartbeat(
                reader.GetString(0),
                SqliteTime.FromStorage(reader.GetString(1))));
        }

        return heartbeats;
    }
}
