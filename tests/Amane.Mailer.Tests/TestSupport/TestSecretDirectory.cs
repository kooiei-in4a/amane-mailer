namespace Amane.Mailer.Tests.TestSupport;

/// <summary>
/// Creates a directory with the same owner-only 0700 mode the register-acs runbook asks
/// operators to configure for the ACS secret / platform sender directories (see
/// <c>FileSystemSafetyGuard.EnsureDirectoryIsSafe</c>). <see cref="Directory.CreateDirectory"/>
/// alone leaves a Linux default mode (typically 0755, group/other-readable) that the production
/// permission check correctly rejects; tests that exercise <c>SecretFileWriter</c>,
/// <c>ExclusiveOperationLock</c>, or <c>AdminProviderRegisterAcsCommand</c> must start from a
/// correctly-provisioned directory rather than tripping that same check as an unrelated failure.
/// A no-op on Windows, where <see cref="File.SetUnixFileMode"/> is unsupported and the production
/// check itself is skipped (see the repository's stated stance that Windows dev/test cannot
/// substitute for Linux owner/mode verification).
/// </summary>
internal static class TestSecretDirectory
{
    public static void CreateSecure(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
