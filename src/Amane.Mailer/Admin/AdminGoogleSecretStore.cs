using Amane.Mailer.Operations;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Admin;

/// <summary>
/// Protected Google Client Secret file under the managed data root.
/// Mirrors the ACS secret pattern (owner-only file) but allows overwrite for rotation.
/// </summary>
internal static class AdminGoogleSecretStore
{
    public const string DefaultSecretDirectory = "/app/data/secrets/admin_google";
    public const string CanonicalFileName = "client_secret";
    public const string SecretPathConfigurationKey = "Mailer:Admin:GoogleClientSecretPath";
    public const string SecretPathEnvKey = "AMANE_ADMIN_GOOGLE_CLIENT_SECRET_FILE";

    public static string DefaultSecretPath =>
        Path.Combine(DefaultSecretDirectory, CanonicalFileName);

    public static string ResolveSecretPath(IConfiguration configuration)
    {
        var configured = configuration[SecretPathConfigurationKey]
            ?? configuration[SecretPathEnvKey];
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        return DefaultSecretPath;
    }

    public static bool TryReadSecret(string path, out string value)
    {
        value = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            FileSystemSafetyGuard.EnsureTargetFileIsSafeIfExists(path);
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                return false;

            FileSystemSafetyGuard.EnsureDirectoryIsSafe(directory);
            var fileSystem = new HostSetupFileSystem();
            if (!fileSystem.IsOwnerOnlyFile(path))
                return false;

            var candidate = File.ReadAllText(path).Trim();
            if (candidate.Length == 0)
                return false;

            value = candidate;
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or SecretOperationException)
        {
            return false;
        }
    }

    public static bool IsSecretConfigured(string? path) =>
        !string.IsNullOrWhiteSpace(path) && TryReadSecret(path, out _);

    /// <summary>
    /// Atomically writes (or overwrites) the Client Secret with owner-only permissions.
    /// </summary>
    public static void WriteSecret(string path, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var trimmed = secret.Trim();
        if (trimmed.Length == 0)
            throw new InvalidOperationException("Google Client Secret must not be empty.");

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Google Client Secret path is invalid.");
        var fileSystem = new HostSetupFileSystem();
        fileSystem.CreateOwnerOnlyDirectory(directory);
        FileSystemSafetyGuard.EnsureDirectoryIsSafe(directory);
        FileSystemSafetyGuard.EnsureTargetFileIsSafeIfExists(path);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
        try
        {
            SecureFileCreate.WriteAllTextCreateNew(temporaryPath, trimmed);
            File.Move(temporaryPath, path, overwrite: true);
            fileSystem.FlushDirectory(directory);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        if (!TryReadSecret(path, out _))
            throw new InvalidOperationException("Google Client Secret write verification failed.");
    }
}
