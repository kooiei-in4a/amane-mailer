using System.Text;

namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminSuppressionCursor(string CreatedAt, Guid Id)
{
    public static string Encode(DateTimeOffset createdAt, Guid id)
    {
        var payload = $"{SqliteTime.ToStorageUtc(createdAt)}:{id:D}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    public static bool TryDecode(string? cursor, out AdminSuppressionCursor decoded)
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

            var createdAt = payload[..separator];
            if (!Guid.TryParse(payload[(separator + 1)..], out var id))
                return false;

            decoded = new AdminSuppressionCursor(createdAt, id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
