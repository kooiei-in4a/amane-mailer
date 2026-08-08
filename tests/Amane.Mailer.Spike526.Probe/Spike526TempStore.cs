namespace Amane.Mailer.Spike526.Probe;

public sealed class Spike526TempStore
{
    private const string Prefix = "spike526-";
    private readonly string _root;

    public Spike526TempStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public string CreateFilePath()
    {
        Directory.CreateDirectory(_root);
        return Path.Combine(_root, Prefix + Guid.NewGuid().ToString("N") + ".tmp");
    }

    public int CountOwnedFiles() =>
        Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, Prefix + "*.tmp", SearchOption.TopDirectoryOnly).Count()
            : 0;

    public long GetOwnedBytes() =>
        Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, Prefix + "*.tmp", SearchOption.TopDirectoryOnly)
                .Sum(static path => new FileInfo(path).Length)
            : 0;

    public int CleanupOwnedFiles()
    {
        if (!Directory.Exists(_root))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(_root, Prefix + "*.tmp", SearchOption.TopDirectoryOnly))
        {
            EnsureOwnedPath(path);
            File.Delete(path);
            deleted++;
        }

        return deleted;
    }

    public Spike526CleanupResult CleanupAndReport(string? outsideFile = null)
    {
        var deleted = CleanupOwnedFiles();
        return new Spike526CleanupResult(
            deleted,
            CountOwnedFiles(),
            outsideFile is null || File.Exists(outsideFile));
    }

    private void EnsureOwnedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(_root, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relative)
            || !Path.GetFileName(fullPath).StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to delete a path outside the Spike526 temp root.");
        }
    }
}
