using System.Globalization;
using System.Security;
using System.Text.Json;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Admin;

/// <summary>
/// Reads the fixed, sanitized receipts written by the host backup scripts and
/// the existing Admin database-operation audit events. It never reads logs or
/// backup contents.
/// </summary>
public sealed class AdminBackupStatusReader(
    MailerAdminBackupStatusOptions options,
    AdminAuditRepository auditRepository,
    TimeProvider timeProvider)
{
    private const int MaxReceiptBytes = 4096;

    public async Task<AdminBackupStatusReadModel> LoadAsync(
        DateTimeOffset? asOfUtc = null,
        CancellationToken cancellationToken = default)
    {
        var now = (asOfUtc ?? timeProvider.GetUtcNow()).ToUniversalTime();
        var databaseOnly = await ReadScriptStatusAsync(
            "database-only",
            "db-only",
            options.DatabaseOnlyStaleAfter,
            now,
            cancellationToken);
        var fullInstance = await ReadScriptStatusAsync(
            "full-instance",
            "full-instance",
            options.FullInstanceStaleAfter,
            now,
            cancellationToken);
        var adminDatabaseBackup = await ReadAdminDatabaseBackupAsync(cancellationToken);

        return new AdminBackupStatusReadModel(
            now,
            adminDatabaseBackup,
            databaseOnly,
            fullInstance,
            RestoreVerificationRecorded: false);
    }

    private async Task<AdminBackupScriptStatus> ReadScriptStatusAsync(
        string backupType,
        string fileStem,
        TimeSpan? staleAfter,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var (attempt, attemptState) = await ReadReceiptAsync(
            $"{fileStem}.attempt.json",
            backupType,
            "attempt",
            cancellationToken);
        var (success, successState) = await ReadReceiptAsync(
            $"{fileStem}.success.json",
            backupType,
            "success",
            cancellationToken);
        var (offsiteSuccess, _) = await ReadReceiptAsync(
            $"{fileStem}.offsite-success.json",
            backupType,
            "offsite-success",
            cancellationToken);

        var lastSuccessAt = success?.CompletedAtUtc;
        var successAge = lastSuccessAt is DateTimeOffset successTimestamp
            ? now - successTimestamp
            : (TimeSpan?)null;
        AdminBackupFreshness freshness;
        if (staleAfter is null)
        {
            freshness = AdminBackupFreshness.Unknown;
        }
        else if (successState == AdminBackupEvidenceState.Invalid)
        {
            freshness = AdminBackupFreshness.Unknown;
        }
        else if (lastSuccessAt is null)
        {
            freshness = AdminBackupFreshness.Stale;
        }
        else if (successAge is null || successAge.Value < TimeSpan.Zero)
        {
            freshness = AdminBackupFreshness.Unknown;
        }
        else
        {
            freshness = successAge.Value > staleAfter.Value
                ? AdminBackupFreshness.Stale
                : AdminBackupFreshness.Fresh;
        }

        return new AdminBackupScriptStatus(
            attemptState,
            successState,
            ReadOutcome(attempt?.Status),
            attempt is null ? null : attempt.CompletedAtUtc ?? attempt.StartedAtUtc,
            attempt?.Stage,
            lastSuccessAt,
            success?.ArtifactName is string artifactName
                ? ReadLocalArtifactPresence(artifactName)
                : null,
            ReadOffsiteState(attempt?.OffsiteStatus),
            offsiteSuccess?.CompletedAtUtc,
            staleAfter is not null,
            freshness);
    }

    private async Task<(AdminBackupStatusReceipt? Receipt, AdminBackupEvidenceState State)> ReadReceiptAsync(
        string fileName,
        string backupType,
        string recordType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.StatusDirectory))
            return (null, AdminBackupEvidenceState.NotRecorded);

        var directoryState = ReadDirectoryState(options.StatusDirectory);
        if (directoryState == FileSystemEvidenceState.Missing)
            return (null, AdminBackupEvidenceState.NotRecorded);
        if (directoryState != FileSystemEvidenceState.Available)
            return (null, AdminBackupEvidenceState.Invalid);

        var path = Path.Combine(options.StatusDirectory, fileName);
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return (null, AdminBackupEvidenceState.NotRecorded);
        }
        catch (DirectoryNotFoundException)
        {
            return (null, AdminBackupEvidenceState.NotRecorded);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, AdminBackupEvidenceState.Invalid);
        }
        catch (IOException)
        {
            return (null, AdminBackupEvidenceState.Invalid);
        }
        catch (SecurityException)
        {
            return (null, AdminBackupEvidenceState.Invalid);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0
            || (attributes & FileAttributes.Directory) != 0
            || HasUnsafeUnixWritePermissions(path))
        {
            return (null, AdminBackupEvidenceState.Invalid);
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is < 1 or > MaxReceiptBytes)
                return (null, AdminBackupEvidenceState.Invalid);

            var buffer = new byte[MaxReceiptBytes + 1];
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(totalRead, buffer.Length - totalRead),
                    cancellationToken);
                if (read == 0)
                    break;
                totalRead += read;
            }

            if (totalRead is < 1 or > MaxReceiptBytes)
                return (null, AdminBackupEvidenceState.Invalid);

            var receipt = JsonSerializer.Deserialize(
                buffer.AsSpan(0, totalRead),
                AdminBackupStatusJsonContext.Default.AdminBackupStatusReceipt);
            return IsValidReceipt(receipt, backupType, recordType)
                ? (receipt, AdminBackupEvidenceState.Available)
                : (null, AdminBackupEvidenceState.Invalid);
        }
        catch (JsonException)
        {
            return (null, AdminBackupEvidenceState.Invalid);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, AdminBackupEvidenceState.Invalid);
        }
        catch (IOException)
        {
            return (null, AdminBackupEvidenceState.Invalid);
        }
        catch (SecurityException)
        {
            return (null, AdminBackupEvidenceState.Invalid);
        }
    }

    private static bool IsValidReceipt(
        AdminBackupStatusReceipt? receipt,
        string backupType,
        string recordType)
    {
        if (receipt is null
            || receipt.SchemaVersion != 1
            || !string.Equals(receipt.BackupType, backupType, StringComparison.Ordinal)
            || !string.Equals(receipt.RecordType, recordType, StringComparison.Ordinal)
            || receipt.StartedAtUtc == default
            || receipt.StartedAtUtc.Offset != TimeSpan.Zero
            || (receipt.CompletedAtUtc is DateTimeOffset completedAt
                && (completedAt.Offset != TimeSpan.Zero || completedAt < receipt.StartedAtUtc)))
        {
            return false;
        }

        return recordType switch
        {
            "attempt" => IsValidAttemptReceipt(receipt),
            "success" => IsValidSuccessReceipt(receipt, backupType, requireOffsiteSuccess: false),
            "offsite-success" => IsValidSuccessReceipt(receipt, backupType, requireOffsiteSuccess: true),
            _ => false,
        };
    }

    private static bool IsValidAttemptReceipt(AdminBackupStatusReceipt receipt)
    {
        var validStatus = receipt.Status is "running" or "succeeded" or "failed";
        var validStage = receipt.Stage is "preflight"
            or "database"
            or "archive"
            or "encrypt"
            or "validate-local"
            or "upload"
            or "cleanup"
            or "complete";
        var validOffsite = receipt.OffsiteStatus is "not-attempted"
            or "pending"
            or "succeeded"
            or "failed"
            or "skipped";
        var validCompletion = receipt.Status == "running"
            ? receipt.CompletedAtUtc is null
            : receipt.CompletedAtUtc is not null;

        return validStatus
            && validStage
            && validOffsite
            && validCompletion
            && (receipt.ArtifactName is null || IsValidArtifactName(receipt.ArtifactName, receipt.BackupType!));
    }

    private static bool IsValidSuccessReceipt(
        AdminBackupStatusReceipt receipt,
        string backupType,
        bool requireOffsiteSuccess) =>
        receipt.Status == "succeeded"
        && receipt.CompletedAtUtc is not null
        && receipt.ArtifactName is not null
        && IsValidArtifactName(receipt.ArtifactName, backupType)
        && (requireOffsiteSuccess
            ? receipt.OffsiteStatus == "succeeded"
            : receipt.OffsiteStatus is "succeeded" or "skipped");

    private bool? ReadLocalArtifactPresence(string artifactName)
    {
        if (string.IsNullOrWhiteSpace(options.StatusDirectory))
            return null;

        var dataDirectory = Path.GetDirectoryName(options.StatusDirectory);
        if (string.IsNullOrEmpty(dataDirectory))
            return null;

        var backupDirectory = Path.Combine(dataDirectory, "backups");
        FileAttributes directoryAttributes;
        try
        {
            directoryAttributes = File.GetAttributes(backupDirectory);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (SecurityException)
        {
            return null;
        }

        if ((directoryAttributes & FileAttributes.Directory) == 0
            || (directoryAttributes & FileAttributes.ReparsePoint) != 0)
        {
            return null;
        }

        try
        {
            var artifactAttributes = File.GetAttributes(Path.Combine(backupDirectory, artifactName));
            return (artifactAttributes & FileAttributes.Directory) == 0
                && (artifactAttributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (SecurityException)
        {
            return null;
        }
    }

    private async Task<AdminOnlineDatabaseBackupStatus> ReadAdminDatabaseBackupAsync(
        CancellationToken cancellationToken)
    {
        var latest = await auditRepository.GetLatestDatabaseBackupOperationAsync(cancellationToken);
        if (latest is null)
        {
            return new AdminOnlineDatabaseBackupStatus(
                AdminBackupEvidenceState.NotRecorded,
                AdminBackupOutcome.Unknown,
                null);
        }

        var outcome = latest.EventType switch
        {
            AdminAuditLog.EventTypes.DbBackupCompleted when latest.Result == AdminAuditLog.Results.Success =>
                AdminBackupOutcome.Succeeded,
            AdminAuditLog.EventTypes.DbBackupFailed => AdminBackupOutcome.Failed,
            _ => AdminBackupOutcome.Unknown,
        };
        return new AdminOnlineDatabaseBackupStatus(
            outcome == AdminBackupOutcome.Unknown
                ? AdminBackupEvidenceState.Invalid
                : AdminBackupEvidenceState.Available,
            outcome,
            latest.OccurredAt);
    }

    private static FileSystemEvidenceState ReadDirectoryState(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0
                || (attributes & FileAttributes.ReparsePoint) != 0
                || HasUnsafeUnixWritePermissions(path))
            {
                return FileSystemEvidenceState.Invalid;
            }

            return FileSystemEvidenceState.Available;
        }
        catch (FileNotFoundException)
        {
            return FileSystemEvidenceState.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return FileSystemEvidenceState.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return FileSystemEvidenceState.Invalid;
        }
        catch (IOException)
        {
            return FileSystemEvidenceState.Invalid;
        }
        catch (SecurityException)
        {
            return FileSystemEvidenceState.Invalid;
        }
    }

    private static bool HasUnsafeUnixWritePermissions(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return false;

        try
        {
            const UnixFileMode groupOrOtherWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
            return (File.GetUnixFileMode(path) & groupOrOtherWrite) != 0;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or PlatformNotSupportedException
            or ArgumentException)
        {
            return true;
        }
    }

    private static bool IsValidArtifactName(string artifactName, string backupType)
    {
        var prefix = backupType == "database-only" ? "mailer-" : "mailer-state-";
        var suffix = backupType == "database-only" ? ".db.age" : ".tar.age";
        if (!artifactName.StartsWith(prefix, StringComparison.Ordinal)
            || !artifactName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var timestampLength = 16;
        if (artifactName.Length != prefix.Length + timestampLength + suffix.Length)
            return false;

        var timestamp = artifactName.AsSpan(prefix.Length, timestampLength);
        return DateTime.TryParseExact(
            timestamp,
            "yyyyMMdd'T'HHmmss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out _);
    }

    private static AdminBackupOutcome ReadOutcome(string? status) => status switch
    {
        "running" => AdminBackupOutcome.Running,
        "succeeded" => AdminBackupOutcome.Succeeded,
        "failed" => AdminBackupOutcome.Failed,
        _ => AdminBackupOutcome.Unknown,
    };

    private static AdminBackupOffsiteState ReadOffsiteState(string? status) => status switch
    {
        "not-attempted" => AdminBackupOffsiteState.NotAttempted,
        "pending" => AdminBackupOffsiteState.Pending,
        "succeeded" => AdminBackupOffsiteState.Succeeded,
        "failed" => AdminBackupOffsiteState.Failed,
        "skipped" => AdminBackupOffsiteState.Skipped,
        _ => AdminBackupOffsiteState.Unknown,
    };

    private enum FileSystemEvidenceState
    {
        Missing,
        Available,
        Invalid,
    }
}
