using Amane.Mailer.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Configuration;

public sealed record MailerAdminBackupStatusOptions
{
    public const int MinStaleAfterHours = 1;
    public const int MaxStaleAfterHours = 87600;

    public string StatusDirectory { get; init; } = string.Empty;

    public TimeSpan? DatabaseOnlyStaleAfter { get; init; }

    public TimeSpan? FullInstanceStaleAfter { get; init; }

    public static MailerAdminBackupStatusOptions Load(
        IConfiguration configuration,
        string mailerConnectionString,
        bool adminEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Backup status is consumed only by Admin. Ignore typos in its optional
        // thresholds while the Admin surface is disabled, as with other Admin options.
        if (!adminEnabled)
            return new();

        var builder = new SqliteConnectionStringBuilder(mailerConnectionString);
        var dataSource = builder.DataSource;
        var statusDirectory = string.IsNullOrWhiteSpace(dataSource)
            || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(dataSource))!,
                ".mailer-backup-status");

        var databaseOnlyHours = ConfigurationIntReader.ReadOptional(
            configuration,
            MinStaleAfterHours,
            MaxStaleAfterHours,
            "MAILER_BACKUP_STATUS_DB_STALE_AFTER_HOURS",
            "AMANE_BACKUP_STATUS_DB_STALE_AFTER_HOURS");
        var fullInstanceHours = ConfigurationIntReader.ReadOptional(
            configuration,
            MinStaleAfterHours,
            MaxStaleAfterHours,
            "MAILER_BACKUP_STATUS_FULL_INSTANCE_STALE_AFTER_HOURS",
            "AMANE_BACKUP_STATUS_FULL_INSTANCE_STALE_AFTER_HOURS");

        return new MailerAdminBackupStatusOptions
        {
            StatusDirectory = statusDirectory,
            DatabaseOnlyStaleAfter = databaseOnlyHours is int databaseOnly
                ? TimeSpan.FromHours(databaseOnly)
                : null,
            FullInstanceStaleAfter = fullInstanceHours is int fullInstance
                ? TimeSpan.FromHours(fullInstance)
                : null,
        };
    }
}
