using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Attachments.Spool;

/// <summary>
/// Durable short-lived attachment spool location (ADR 0022 D-08). Defaults to a sibling
/// directory of the configured SQLite database file, so it shares the same local deployment
/// boundary / persistent volume and survives process/container restarts.
/// </summary>
public sealed class AttachmentSpoolOptions
{
    public required string RootDirectory { get; init; }

    public string StagingRoot => Path.Combine(RootDirectory, "staging");

    public string CommittedRoot => Path.Combine(RootDirectory, "committed");

    public static AttachmentSpoolOptions Resolve(IConfiguration configuration, SqliteConnectionFactory connections)
    {
        var configured = configuration["Mailer:Attachments:SpoolDirectory"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new AttachmentSpoolOptions { RootDirectory = Path.GetFullPath(configured) };
        }

        var databasePath = connections.GetConfiguredDatabasePath();
        var baseDirectory = databasePath is not null
            ? Path.GetDirectoryName(Path.GetFullPath(databasePath))
            : null;
        var root = baseDirectory is not null
            ? Path.Combine(baseDirectory, "attachment-spool")
            : Path.Combine(AppContext.BaseDirectory, "attachment-spool");

        return new AttachmentSpoolOptions { RootDirectory = root };
    }
}
