using System.Globalization;

namespace Amane.Mailer.Data.Sqlite;

public static class SqliteTime
{
    public const string StorageFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    public static string ToStorageUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(StorageFormat, CultureInfo.InvariantCulture);

    public static DateTimeOffset FromStorage(string value) =>
        DateTimeOffset.ParseExact(
            value,
            StorageFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
