namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Thrown by <see cref="SqliteConnectionFactory.BackupToAsync"/> when its caller's
/// <c>verifyBeforePublish</c> hook reports that the backup maintenance lease was lost during a
/// long-running snapshot (ADR 0022 D-09) -- the snapshot data is discarded and never published
/// as the real backup file.
/// </summary>
public sealed class BackupMaintenanceLeaseLostException()
    : Exception("Backup maintenance lease was lost before the snapshot could be published.");
