using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Read-side queries for canonical attachment metadata (ADR 0022 D-08). Writes happen inline
/// in <see cref="MailRequestAcceptStore.InsertAcceptedAsync"/>, in the same SQLite transaction
/// as the owning <c>mail_requests</c> row.
/// </summary>
public sealed class MailRequestAttachmentStore(SqliteConnectionFactory connections)
{
    public async Task<IReadOnlyList<AttachmentMetadataRow>> ListByRequestIdAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT attachment_order, file_name, content_type, byte_length, content_sha256, spool_key
            FROM mail_request_attachments
            WHERE request_id = @RequestId
            ORDER BY attachment_order ASC;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));

        var rows = new List<AttachmentMetadataRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AttachmentMetadataRow(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                Guid.Parse(reader.GetString(5))));
        }

        return rows;
    }
}
