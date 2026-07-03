using System.Text;

namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminAuditEventCursor(string OccurredAt, long Id)
{
    public static string Encode(DateTimeOffset occurredAt, long id)
    {
        var payload = $"{SqliteTime.ToStorageUtc(occurredAt)}:{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    public static bool TryDecode(string? cursor, out AdminAuditEventCursor decoded)
    {
        decoded = default!;
        if (string.IsNullOrWhiteSpace(cursor))
            return false;

        try
        {
            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var separator = payload.LastIndexOf(':');
            if (separator <= 0 || separator == payload.Length - 1)
                return false;

            var occurredAt = payload[..separator];
            if (!long.TryParse(
                    payload[(separator + 1)..],
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var id))
            {
                return false;
            }

            decoded = new AdminAuditEventCursor(occurredAt, id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
