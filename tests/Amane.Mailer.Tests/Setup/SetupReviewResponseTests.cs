using System.Diagnostics;
using System.Text;
using Amane.Mailer.Operations;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Tests.Setup;

public sealed class SetupReviewResponseTests
{
    [Fact]
    public void Integrity_seal_does_not_verify_when_copied_to_another_bundle_identity()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var core = new SetupCore(bundleIdFactory: static () => "bundle-a");
            var result = core.GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root));
            Assert.Equal(SetupResultCode.Succeeded, result.Code);

            var bundleRoot = SetupBundleLayout.BundleRoot(root, "bundle-a");
            var secretsEnv = File.ReadAllBytes(Path.Combine(bundleRoot, "env", "secrets.env"));
            var seal = File.ReadAllBytes(Path.Combine(bundleRoot, "metadata", "integrity.seal"));
            var key = File.ReadAllBytes(SetupBundleLayout.HostSealingKeyPath(root));
            var members = new List<(string, byte[])> { ("env/secrets.env", secretsEnv) };

            Assert.True(SetupIntegritySealer.TryVerifySeal(
                key,
                seal,
                "bundle-a",
                result.ConfigurationFingerprint!,
                SetupBundleLayout.RecordedSchemaVersion,
                members));

            Assert.False(SetupIntegritySealer.TryVerifySeal(
                key,
                seal,
                "bundle-b",
                result.ConfigurationFingerprint!,
                SetupBundleLayout.RecordedSchemaVersion,
                members));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Manual_markers_fail_closed_even_when_bundles_directory_exists()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "bundles"));
            File.WriteAllText(Path.Combine(root, ".env"), "COMPOSE_PROJECT_NAME=manual");
            var result = new SetupCore(bundleIdFactory: static () => "x")
                .GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root, dryRun: true));
            Assert.Equal(SetupResultCode.RejectedConflictManual, result.Code);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void DryRun_still_runs_conflict_preflight_for_duplicate_bundle_id()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var core = new SetupCore(bundleIdFactory: static () => "dup-dry");
            Assert.Equal(SetupResultCode.Succeeded, core.GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root)).Code);
            var dry = core.GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root, dryRun: true));
            Assert.Equal(SetupResultCode.RejectedBundleExists, dry.Code);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Public_env_overrides_reject_workflow_owned_keys()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-ov-" + Guid.NewGuid().ToString("N")));
        var request = SetupTestFixtures.LocalMailpitRequest(root, dryRun: true);
        request = new SetupRequest
        {
            Mode = request.Mode,
            ManagedRootPath = request.ManagedRootPath,
            DryRun = true,
            Tenants = request.Tenants,
            TokenSecrets = request.TokenSecrets,
            PublicEnvOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AMANE_ADMIN_ENABLED"] = "true",
                ["MAILER_BOUNCE_INGESTION"] = "queue",
            },
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
    }

    [Fact]
    public void Admin_enabled_typed_representation_is_rejected()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-adm-" + Guid.NewGuid().ToString("N")));
        var request = SetupTestFixtures.LocalMailpitRequest(root, dryRun: true);
        request = new SetupRequest
        {
            Mode = request.Mode,
            ManagedRootPath = request.ManagedRootPath,
            DryRun = true,
            Tenants = request.Tenants,
            TokenSecrets = request.TokenSecrets,
            Admin = new SetupAdminBootstrapRepresentation { Enabled = true, AllowHttp = true },
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
    }

    [Fact]
    public void Admin_password_hash_token_secret_is_rejected()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-hash-" + Guid.NewGuid().ToString("N")));
        var request = SetupTestFixtures.LocalMailpitRequest(root, dryRun: true);
        var secrets = new Dictionary<string, string>(request.TokenSecrets, StringComparer.Ordinal)
        {
            ["AMANE_ADMIN_PASSWORD_HASH"] = "pbkdf2:sha256:synthetic-not-real",
        };
        request = new SetupRequest
        {
            Mode = request.Mode,
            ManagedRootPath = request.ManagedRootPath,
            DryRun = true,
            Tenants = request.Tenants,
            TokenSecrets = secrets,
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
        Assert.Contains("Admin password hash", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_sealing_key_with_existing_bundle_fails_closed()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var core = new SetupCore(bundleIdFactory: static () => "first");
            Assert.Equal(SetupResultCode.Succeeded, core.GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root)).Code);
            File.Delete(SetupBundleLayout.HostSealingKeyPath(root));

            var second = new SetupCore(bundleIdFactory: static () => "second")
                .GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root, dryRun: true));
            Assert.Equal(SetupResultCode.RejectedSealingKeyMissing, second.Code);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void World_readable_sealing_key_is_rejected_on_linux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Unix mode verification is Linux-only.");
            return;
        }

        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            Directory.CreateDirectory(SetupBundleLayout.SealingDir(root));
            var keyPath = SetupBundleLayout.HostSealingKeyPath(root);
            File.WriteAllBytes(keyPath, new byte[SetupIntegritySealer.SealingKeyLength]);
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

            var result = new SetupCore(bundleIdFactory: static () => "perm")
                .GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root, dryRun: true));
            Assert.Equal(SetupResultCode.RejectedSealingKeyUnsafe, result.Code);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Weakened_sealing_key_acl_is_rejected_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows ACL verification is Windows-only.");
            return;
        }

        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            Directory.CreateDirectory(SetupBundleLayout.SealingDir(root));
            var keyPath = SetupBundleLayout.HostSealingKeyPath(root);
            SecureFileCreate.WriteAllBytesCreateNew(keyPath, new byte[SetupIntegritySealer.SealingKeyLength]);

            var grant = new ProcessStartInfo
            {
                FileName = "icacls.exe",
                ArgumentList = { keyPath, "/grant", @"BUILTIN\Users:(R)" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var process = Process.Start(grant) ?? throw new InvalidOperationException("icacls failed to start."))
            {
                _ = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                Assert.Equal(0, process.ExitCode);
            }

            Assert.False(new HostSetupFileSystem().IsOwnerOnlyFile(keyPath));

            var result = new SetupCore(bundleIdFactory: static () => "win-acl")
                .GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root, dryRun: true));
            Assert.Equal(SetupResultCode.RejectedSealingKeyUnsafe, result.Code);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Owner_only_inspection_failure_is_fail_closed()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-ownfail-" + Guid.NewGuid().ToString("N")));
        var inner = new HostSetupFileSystem();
        var fs = new OwnerOnlyAlwaysFalseFileSystem(inner);
        // Pre-create a sealing key so preflight reaches permission verification.
        Directory.CreateDirectory(SetupBundleLayout.SealingDir(root));
        SecureFileCreate.WriteAllBytesCreateNew(
            SetupBundleLayout.HostSealingKeyPath(root),
            new byte[SetupIntegritySealer.SealingKeyLength]);

        try
        {
            var result = new SetupCore(fs, bundleIdFactory: static () => "own-fail")
                .GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root, dryRun: true));
            Assert.Equal(SetupResultCode.RejectedSealingKeyUnsafe, result.Code);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Runtime_uid_child_process_can_read_generated_files_on_linux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Cross-UID read proof is Linux-only.");
            return;
        }

        var euid = new HostSetupFileSystem().GetEffectiveUnixUserId() ?? 0;
        if (euid != 0)
        {
            Assert.Skip("Cross-UID read proof requires root to chown to a distinct runtime UID.");
            return;
        }

        if (!IsCommandAvailable("setpriv"))
        {
            Assert.Skip("setpriv is required to drop to the runtime UID.");
            return;
        }

        const uint runtimeUid = 1654;
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var request = SetupTestFixtures.StagingAcsRequest(root);
            request = new SetupRequest
            {
                Mode = request.Mode,
                ManagedRootPath = request.ManagedRootPath,
                DryRun = false,
                Tenants = request.Tenants,
                TokenSecrets = request.TokenSecrets,
                MetricsBearerToken = request.MetricsBearerToken,
                AcsConnectionString = request.AcsConnectionString,
                PlatformSender = request.PlatformSender,
                RuntimeFileOwnership = new SetupRuntimeFileOwnership
                {
                    UnixUserId = runtimeUid,
                    UnixGroupId = runtimeUid,
                },
            };

            var result = new SetupCore(bundleIdFactory: static () => "uid-read")
                .GenerateBundle(request);
            Assert.Equal(SetupResultCode.Succeeded, result.Code);

            var bundleRoot = SetupBundleLayout.BundleRoot(root, "uid-read");
            // Production bind-mounts expose leaf paths without requiring APP_UID to traverse the
            // host managed-root owner tree. Grant execute-only on the test-created root so the
            // setpriv child can reach the runtime-owned leaf files.
            var rootMode = File.GetUnixFileMode(root);
            File.SetUnixFileMode(
                root,
                rootMode | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

            AssertReadableAsUid(runtimeUid, Path.Combine(bundleRoot, "config", "tenants.json"));
            AssertReadableAsUid(runtimeUid, Path.Combine(bundleRoot, "secrets", AcsSecretFileNames.CanonicalFileName));
            AssertReadableAsUid(runtimeUid, Path.Combine(bundleRoot, "metadata", "recorded.json"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void AssertReadableAsUid(uint uid, string path)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "setpriv",
            ArgumentList =
            {
                $"--reuid={uid}",
                $"--regid={uid}",
                "--clear-groups",
                "--",
                "head",
                "-c",
                "1",
                path,
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("setpriv failed to start.");
        _ = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"runtime uid could not read {Path.GetFileName(path)}: {stderr}");
    }

    private static bool IsCommandAvailable(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sh",
            ArgumentList = { "-c", $"command -v {command}" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi);
        if (process is null)
        {
            return false;
        }

        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private sealed class OwnerOnlyAlwaysFalseFileSystem(ISetupFileSystem inner) : ISetupFileSystem
    {
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public bool FileExists(string path) => inner.FileExists(path);
        public SetupLinkInspectionResult InspectSymlinkOrReparsePoint(string path) =>
            inner.InspectSymlinkOrReparsePoint(path);
        public IEnumerable<string> EnumerateFileSystemEntries(string path) => inner.EnumerateFileSystemEntries(path);
        public void CreateOwnerOnlyDirectory(string path) => inner.CreateOwnerOnlyDirectory(path);
        public void WriteProtectedFileCreateNew(string path, ReadOnlySpan<byte> content) =>
            inner.WriteProtectedFileCreateNew(path, content);
        public void WriteProtectedFileCreateNew(string path, string content) =>
            inner.WriteProtectedFileCreateNew(path, content);
        public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);
        public void DeleteFile(string path) => inner.DeleteFile(path);
        public void DeleteDirectoryRecursive(string path) => inner.DeleteDirectoryRecursive(path);
        public void MoveReplace(string sourcePath, string destinationPath) =>
            inner.MoveReplace(sourcePath, destinationPath);
        public void FlushDirectory(string path) => inner.FlushDirectory(path);
        public void FlushFile(string path) => inner.FlushFile(path);
        public void SetUnixOwnership(string path, uint userId, uint groupId) =>
            inner.SetUnixOwnership(path, userId, groupId);
        public void SetUnixFileModeOwnerOnly(string path, bool executableDirectory) =>
            inner.SetUnixFileModeOwnerOnly(path, executableDirectory);
        public bool TryGetUnixFileMode(string path, out UnixFileMode mode) =>
            inner.TryGetUnixFileMode(path, out mode);
        public bool IsOwnerOnlyFile(string path) => false;
        public uint? GetEffectiveUnixUserId() => inner.GetEffectiveUnixUserId();
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
        }
    }
}
