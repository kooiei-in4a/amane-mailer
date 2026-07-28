using Amane.Mailer.Configuration;

namespace Amane.Mailer.Setup;

/// <summary>
/// Non-interactive AOT/path smoke for Setup Core. Uses dry-run only so no plaintext secrets are
/// written under shared temp directories. Does not activate bundles or operate Docker.
/// </summary>
public static class SetupCoreSelfCheckCommand
{
    public const int SuccessExitCode = 0;
    public const int FailureExitCode = 1;
    public const int UsageErrorExitCode = 2;

    public static bool IsSelfCheckCommand(IReadOnlyList<string> args) =>
        args.Count == 2
        && string.Equals(args[0], "setup", StringComparison.Ordinal)
        && string.Equals(args[1], "core-self-check", StringComparison.Ordinal);

    public static Task<int> ExecuteAsync(TextWriter output, TextWriter error)
    {
        try
        {
            var managedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-setup-core-self-check"));
            var tenants = new MailerTenantsFile
            {
                Version = 1,
                Environment = "develop",
                Tenants =
                [
                    new MailerTenant
                    {
                        TenantId = Guid.Parse("00000000-0000-0000-0000-000000000101"),
                        Name = "self-check",
                        SourceServices = ["self-check-service"],
                        DefaultFrom = new MailerAddress
                        {
                            Email = "noreply@example.com",
                            DisplayName = "Self Check",
                        },
                        TokenEnv = "MAIL_SERVICE_TOKEN",
                        Provider = "mailpit",
                        LiveSending = false,
                        Retry = new MailerRetryOptions
                        {
                            MaxAttempts = 3,
                            InitialDelaySeconds = 1,
                            MaxDelaySeconds = 10,
                        },
                    },
                ],
            };

            var request = new SetupRequest
            {
                Mode = SetupMode.LocalMailpit,
                ManagedRootPath = managedRoot,
                DryRun = true,
                Tenants = tenants,
                TokenSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["MAIL_SERVICE_TOKEN"] = "synthetic-self-check-token-not-real",
                },
                MetricsBearerToken = "synthetic-self-check-metrics-token-not-real",
            };

            var core = new SetupCore(bundleIdFactory: static () => "selfcheck-00000001");
            var first = core.GenerateBundle(request);
            var second = core.GenerateBundle(request);

            if (first.Code != SetupResultCode.DryRunPlan || second.Code != SetupResultCode.DryRunPlan)
            {
                error.WriteLine("setup core-self-check failed: unexpected result code.");
                return Task.FromResult(FailureExitCode);
            }

            if (!string.Equals(first.ConfigurationFingerprint, second.ConfigurationFingerprint, StringComparison.Ordinal))
            {
                error.WriteLine("setup core-self-check failed: fingerprint was not deterministic.");
                return Task.FromResult(FailureExitCode);
            }

            var bundlePath = SetupBundleLayout.BundleRoot(managedRoot, first.BundleId!);
            if (Directory.Exists(bundlePath))
            {
                error.WriteLine("setup core-self-check failed: dry-run wrote a bundle directory.");
                return Task.FromResult(FailureExitCode);
            }

            output.WriteLine(
                $"success: operation=setup_core_self_check result={SetupResultCode.DryRunPlan} fingerprint={first.ConfigurationFingerprint}");
            return Task.FromResult(SuccessExitCode);
        }
        catch
        {
            error.WriteLine("setup core-self-check failed: unexpected diagnostic error (details omitted).");
            return Task.FromResult(FailureExitCode);
        }
    }
}
