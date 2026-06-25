using System.Text;

namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminDeadLetterCursor(string CompletedAt, Guid Id)
{
    public static string Encode(DateTimeOffset completedAt, Guid id)
    {
        var payload = $"{SqliteTime.ToStorageUtc(completedAt)}:{id:D}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    public static bool TryDecode(string? cursor, out AdminDeadLetterCursor decoded)
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

            var completedAt = payload[..separator];
            if (!Guid.TryParse(payload[(separator + 1)..], out var id))
                return false;

            decoded = new AdminDeadLetterCursor(completedAt, id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
