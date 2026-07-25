using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Admin;

public sealed record MailerAdminDbOpsOptions
{
    public bool Enabled { get; init; }

    public string BackupDirectory { get; init; } = string.Empty;

    public static MailerAdminDbOpsOptions Load(
        IConfiguration configuration,
        string mailerConnectionString,
        bool adminEnabled = true)
    {
        // DbOps is Admin-only. When Admin is off, ignore DbOps env typos so mail delivery still starts.
        if (!adminEnabled)
            return new() { Enabled = false };

        var enabled = ConfigurationBooleanReader.Read(
            configuration,
            defaultValue: false,
            "AMANE_ADMIN_DB_OPS_ENABLED",
            "MAILER_ADMIN_DB_OPS_ENABLED");

        var backupDirectory = ReadString(
            configuration,
            "AMANE_ADMIN_DB_BACKUP_DIRECTORY",
            "MAILER_ADMIN_DB_BACKUP_DIRECTORY",
            string.Empty);

        if (enabled && string.IsNullOrWhiteSpace(backupDirectory))
        {
            backupDirectory = DeriveDefaultBackupDirectory(mailerConnectionString);
        }

        return new()
        {
            Enabled = enabled,
            BackupDirectory = string.IsNullOrWhiteSpace(backupDirectory)
                ? string.Empty
                : Path.GetFullPath(backupDirectory),
        };
    }

    public void Validate(bool adminEnabled, SqliteConnectionFactory connections)
    {
        if (!Enabled)
            return;

        if (!adminEnabled)
        {
            throw new InvalidOperationException(
                "AMANE_ADMIN_DB_OPS_ENABLED=true requires AMANE_ADMIN_ENABLED=true.");
        }

        if (string.IsNullOrWhiteSpace(BackupDirectory))
        {
            throw new InvalidOperationException(
                "AMANE_ADMIN_DB_BACKUP_DIRECTORY must be set when AMANE_ADMIN_DB_OPS_ENABLED=true.");
        }

        if (!Path.IsPathRooted(BackupDirectory))
        {
            throw new InvalidOperationException(
                "AMANE_ADMIN_DB_BACKUP_DIRECTORY must be an absolute path.");
        }

        if (connections.IsConfiguredDatabasePath(BackupDirectory))
        {
            throw new InvalidOperationException(
                "AMANE_ADMIN_DB_BACKUP_DIRECTORY must not be the active mailer database path.");
        }

        var normalizedBackupDirectory = Path.GetFullPath(BackupDirectory);
        var databasePath = connections.GetConfiguredDatabasePath();
        if (databasePath is not null)
        {
            var databaseDirectory = Path.GetDirectoryName(databasePath);
            if (databaseDirectory is not null
                && PathsEqual(Path.GetFullPath(databaseDirectory), normalizedBackupDirectory))
            {
                throw new InvalidOperationException(
                    "AMANE_ADMIN_DB_BACKUP_DIRECTORY must not be the active mailer database directory.");
            }
        }
    }

    internal static string DeriveDefaultBackupDirectory(string mailerConnectionString)
    {
        var dataSource = new SqliteConnectionStringBuilder(mailerConnectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource)
            || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "AMANE_ADMIN_DB_BACKUP_DIRECTORY must be configured when the mailer database is in-memory.");
        }

        var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (string.IsNullOrEmpty(databaseDirectory))
        {
            throw new InvalidOperationException(
                "AMANE_ADMIN_DB_BACKUP_DIRECTORY must be configured when the mailer database directory cannot be resolved.");
        }

        return Path.Combine(databaseDirectory, "backups");
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            comparison);
    }

    private static string ReadString(
        IConfiguration configuration,
        string primaryKey,
        string fallbackKey,
        string defaultValue)
    {
        var value = configuration[primaryKey];
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        value = configuration[fallbackKey];
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

}
