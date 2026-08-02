using System.Diagnostics;
using System.Text;
using Amane.Mailer.Configuration;
using Amane.Mailer.Operations;
using Amane.Mailer.Setup;
using Amane.Mailer.Tests.TestSupport;

namespace Amane.Mailer.Tests.Setup;

public sealed class SetupRound3ReviewTests
{
    [Fact]
    public void SecureFileCreate_deletes_incomplete_file_when_write_fails()
    {
        var dir = Path.Combine(Path.GetTempPath(), "amane-scf-" + Guid.NewGuid().ToString("N"));
        TestSecretDirectory.CreateSecure(dir);
        var path = Path.Combine(dir, "partial.bin");
        try
        {
            var ex = Assert.Throws<SecureFileWriteException>(() =>
                SecureFileCreate.WriteAllBytesCreateNewForTests(
                    path,
                    "secret"u8,
                    openStream: p => new ThrowingWriteStream(SecureFileCreate.OpenCreateNewWriteStream(p)),
                    deleteFile: File.Delete));

            Assert.False(ex.CreatedFileCleanupFailed);
            Assert.False(File.Exists(path));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void SecureFileCreate_reports_cleanup_failure_when_delete_fails()
    {
        var dir = Path.Combine(Path.GetTempPath(), "amane-scf2-" + Guid.NewGuid().ToString("N"));
        TestSecretDirectory.CreateSecure(dir);
        var path = Path.Combine(dir, "stuck.bin");
        try
        {
            var ex = Assert.Throws<SecureFileWriteException>(() =>
                SecureFileCreate.WriteAllBytesCreateNewForTests(
                    path,
                    "secret"u8,
                    openStream: p => new ThrowingWriteStream(SecureFileCreate.OpenCreateNewWriteStream(p)),
                    deleteFile: _ => throw new IOException("simulated delete failure")));

            Assert.True(ex.CreatedFileCleanupFailed);
            Assert.True(File.Exists(path));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Mode_environment_mismatch_is_rejected()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-mode-" + Guid.NewGuid().ToString("N")));
        var request = SetupTestFixtures.StagingAcsRequest(root, dryRun: true);
        request = new SetupRequest
        {
            Mode = SetupMode.ProductionAcs,
            ManagedRootPath = request.ManagedRootPath,
            DryRun = true,
            Tenants = request.Tenants,
            TokenSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MAIL_SERVICE_TOKEN_STAGING"] = "synthetic-staging-token-not-real",
            },
            MetricsBearerToken = request.MetricsBearerToken,
            AcsConnectionString = request.AcsConnectionString,
            PlatformSender = request.PlatformSender,
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
    }

    [Fact]
    public void Live_sending_true_is_rejected()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-live-" + Guid.NewGuid().ToString("N")));
        var baseRequest = SetupTestFixtures.StagingAcsRequest(root, dryRun: true);
        var tenants = SetupTestFixtures.AcsStagingTenants();
        tenants = new MailerTenantsFile
        {
            Version = tenants.Version,
            Environment = tenants.Environment,
            Tenants = [tenants.Tenants[0] with { LiveSending = true }],
        };

        var request = new SetupRequest
        {
            Mode = baseRequest.Mode,
            ManagedRootPath = baseRequest.ManagedRootPath,
            DryRun = true,
            Tenants = tenants,
            TokenSecrets = baseRequest.TokenSecrets,
            MetricsBearerToken = baseRequest.MetricsBearerToken,
            AcsConnectionString = baseRequest.AcsConnectionString,
            PlatformSender = baseRequest.PlatformSender,
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
        Assert.Contains("live_sending", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Allowed_host_suffixes_are_included_in_fingerprint_order_independently()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-managed-placeholder"));
        var a = RequestWithWebhookSuffixes(root, ["api.example.com", "hooks.example.com"]);
        var b = RequestWithWebhookSuffixes(root, ["hooks.example.com", "api.example.com"]);
        var c = RequestWithWebhookSuffixes(root, ["api.example.com", "other.example.com"]);

        var fa = SetupConfigurationMaterializer.Materialize(a, "b1", DateTimeOffset.Parse("2026-07-28T00:00:00Z"));
        var fb = SetupConfigurationMaterializer.Materialize(b, "b1", DateTimeOffset.Parse("2026-07-28T00:00:00Z"));
        var fc = SetupConfigurationMaterializer.Materialize(c, "b1", DateTimeOffset.Parse("2026-07-28T00:00:00Z"));

        Assert.Equal(fa.ConfigurationFingerprint, fb.ConfigurationFingerprint);
        Assert.NotEqual(fa.ConfigurationFingerprint, fc.ConfigurationFingerprint);
    }

    [Fact]
    public void Env_file_escapes_dollar_quotes_and_backslash_for_literal_compose_values()
    {
        var text = SetupConfigurationMaterializer.FormatEnvFile(new Dictionary<string, string>
        {
            ["MAIL_SERVICE_TOKEN"] = "pre$NAME${OTHER}a\\b\"c",
        });

        Assert.Equal("MAIL_SERVICE_TOKEN=\"pre$$NAME$${OTHER}a\\\\b\\\"c\"\n", text);
    }

    [Fact]
    public void Metrics_enabled_without_bearer_is_rejected()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-met-" + Guid.NewGuid().ToString("N")));
        var request = SetupTestFixtures.LocalMailpitRequest(root, dryRun: true);
        request = new SetupRequest
        {
            Mode = request.Mode,
            ManagedRootPath = request.ManagedRootPath,
            DryRun = true,
            Tenants = request.Tenants,
            TokenSecrets = request.TokenSecrets,
            MetricsBearerToken = null,
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
    }

    [Fact]
    public void Webhook_secret_env_must_be_supplied_exactly()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-wh-" + Guid.NewGuid().ToString("N")));
        var request = RequestWithWebhookSuffixes(root, ["hooks.example.com"]);
        request = new SetupRequest
        {
            Mode = request.Mode,
            ManagedRootPath = request.ManagedRootPath,
            DryRun = true,
            Tenants = request.Tenants,
            TokenSecrets = request.TokenSecrets,
            MetricsBearerToken = request.MetricsBearerToken,
            WebhookSecrets = new Dictionary<string, string>(StringComparer.Ordinal),
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
    }


    [Fact]
    public void Unknown_callback_allow_ace_type_is_rejected_by_sddl_parser()
    {
        const string owner = "S-1-5-21-1-2-3-1001";
        var ownerOnly = $"O:{owner}G:S-1-5-32-544D:P(A;;FA;;;{owner})";
        var withCallback = $"O:{owner}G:S-1-5-32-544D:P(A;;FA;;;{owner})(XA;;FR;;;WD;(true))";

        Assert.True(HostSetupFileSystem.IsOwnerOnlySddl(ownerOnly, owner));
        Assert.False(HostSetupFileSystem.IsOwnerOnlySddl(withCallback, owner));
    }
    [Fact]
    public void Weakened_windows_acl_is_not_owner_only()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows ACL verifier is Windows-only.");
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "amane-xa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "key");
        try
        {
            SecureFileCreate.WriteAllBytesCreateNew(path, new byte[32]);
            Assert.True(new HostSetupFileSystem().IsOwnerOnlyFile(path));

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "icacls.exe",
                ArgumentList = { path, "/grant", @"BUILTIN\Users:(R)" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            process.WaitForExit();
            Assert.False(new HostSetupFileSystem().IsOwnerOnlyFile(path));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    private static SetupRequest RequestWithWebhookSuffixes(string root, string[] suffixes)
    {
        var tenants = SetupTestFixtures.LocalMailpitTenants();
        tenants = new MailerTenantsFile
        {
            Version = tenants.Version,
            Environment = tenants.Environment,
            Tenants =
            [
                tenants.Tenants[0] with
                {
                    Webhook = new MailerWebhookConfig
                    {
                        Url = "https://hooks.example.com/mail",
                        SecretEnv = "WEBHOOK_SIGNING_SECRET",
                        AllowedHostSuffixes = suffixes,
                    },
                },
            ],
        };

        return new SetupRequest
        {
            Mode = SetupMode.LocalMailpit,
            ManagedRootPath = root,
            DryRun = true,
            Tenants = tenants,
            TokenSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MAIL_SERVICE_TOKEN"] = "synthetic-mail-token-not-real",
            },
            MetricsBearerToken = "synthetic-metrics-token-not-real",
            WebhookSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["WEBHOOK_SIGNING_SECRET"] = "synthetic-webhook-secret-not-real",
            },
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class ThrowingWriteStream(FileStream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new IOException("simulated flush failure after create");
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("simulated write failure after create");
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
