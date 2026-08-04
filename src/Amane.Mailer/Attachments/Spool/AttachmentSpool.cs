namespace Amane.Mailer.Attachments.Spool;

/// <summary>
/// Filesystem operations for the durable short-lived attachment spool (ADR 0022 D-08).
/// Directory/file names are opaque (request id + per-attachment spool key, both
/// Mailer-generated GUIDs) -- never the original filename, tenant id, recipient, or subject.
/// </summary>
public sealed class AttachmentSpool(AttachmentSpoolOptions options)
{
    public string GetStagingDirectory(Guid requestId) =>
        Path.Combine(options.StagingRoot, requestId.ToString("D"));

    public string GetCommittedDirectory(Guid requestId) =>
        Path.Combine(options.CommittedRoot, requestId.ToString("D"));

    public string GetStagingFilePath(Guid requestId, Guid spoolKey) =>
        Path.Combine(GetStagingDirectory(requestId), spoolKey.ToString("D") + ".bin");

    public string GetCommittedFilePath(Guid requestId, Guid spoolKey) =>
        Path.Combine(GetCommittedDirectory(requestId), spoolKey.ToString("D") + ".bin");

    public void EnsureRootDirectoriesExist()
    {
        Directory.CreateDirectory(options.StagingRoot);
        Directory.CreateDirectory(options.CommittedRoot);
    }

    public void EnsureStagingDirectory(Guid requestId) =>
        Directory.CreateDirectory(GetStagingDirectory(requestId));

    /// <summary>
    /// Atomically renames the whole request-scoped staging directory into the committed root
    /// (ADR 0022 D-08 acceptance step 4). Must be called only after every attachment in the
    /// request has been decoded and validated; the caller commits the SQLite transaction after
    /// this succeeds, never before (D-04 step 11 precedes step 12).
    /// </summary>
    public void CommitStagingToCommitted(Guid requestId)
    {
        Directory.CreateDirectory(options.CommittedRoot);
        Directory.Move(GetStagingDirectory(requestId), GetCommittedDirectory(requestId));
    }

    public bool CommittedDirectoryExists(Guid requestId) => Directory.Exists(GetCommittedDirectory(requestId));

    public bool CommittedFileExists(Guid requestId, Guid spoolKey) =>
        File.Exists(GetCommittedFilePath(requestId, spoolKey));

    public void TryDeleteStaging(Guid requestId) => TryDeleteDirectory(GetStagingDirectory(requestId));

    public void TryDeleteCommitted(Guid requestId) => TryDeleteDirectory(GetCommittedDirectory(requestId));

    public IEnumerable<string> EnumerateStagingDirectories()
    {
        if (!Directory.Exists(options.StagingRoot))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(options.StagingRoot))
        {
            yield return directory;
        }
    }

    public IEnumerable<Guid> EnumerateCommittedRequestIds()
    {
        if (!Directory.Exists(options.CommittedRoot))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(options.CommittedRoot))
        {
            if (Guid.TryParseExact(Path.GetFileName(directory), "D", out var requestId))
            {
                yield return requestId;
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort: startup/periodic reconciliation retries (ADR 0022 D-08 spool lifecycle).
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort: startup/periodic reconciliation retries.
        }
    }
}
