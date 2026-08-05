using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Canonical recipient reads for provider dispatch (ADR 0023 D-03/D-10). Writes happen inline in
/// <see cref="MailRequestAcceptStore.InsertAcceptedAsync"/> at accept time and, for the
/// suppression precheck / provider disposition lifecycle, in <see cref="MailPlainSubmissionStore"/>.
/// </summary>
public sealed class MailRequestRecipientStore(SqliteConnectionFactory connections)
{
    /// <summary>
    /// Ordered role ASC (To=0, Cc=1, Bcc=2), then ordinal ASC within role -- i.e. already in the
    /// provider global order To -&gt; Cc -&gt; Bcc with role-internal submission order preserved
    /// (ADR 0023 D-01).
    /// </summary>
    public async Task<IReadOnlyList<MailRequestRecipientRow>> ListByRequestIdAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        return await ListWithinConnectionAsync(connection, requestId, cancellationToken);
    }

    internal static async Task<IReadOnlyList<MailRequestRecipientRow>> ListWithinConnectionAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT recipient_role, ordinal, address, address_key, display_name, delivery_state
            FROM mail_request_recipients
            WHERE request_id = @RequestId
            ORDER BY recipient_role ASC, ordinal ASC;
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));

        var rows = new List<MailRequestRecipientRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new MailRequestRecipientRow(
                (MailRecipientRole)reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                (MailRecipientDeliveryState)reader.GetInt32(5)));
        }

        return rows;
    }
}
