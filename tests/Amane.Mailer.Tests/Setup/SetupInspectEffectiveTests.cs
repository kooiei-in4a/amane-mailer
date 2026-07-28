using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Configuration;
using Amane.Mailer.Setup;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests.Setup;

public sealed class SetupInspectEffectiveTests
{
    private const string SecretCanary = "endpoint=https://canary.example/;accesskey=SECRET_CANARY_VALUE_DO_NOT_LEAK";
    private const string TokenCanary = "tenant-token-canary-DO-NOT-LEAK";
    private const string PathCanary = @"C:\Users\private\secrets\acs_connection_string";

    [Fact]
    public async Task Manual_deployment_without_metadata_returns_not_managed()
    {
        using var dir = new TempDir();
        var tenantsPath = WriteTenants(dir.Path, SetupTestFixtures.LocalMailpitTenants());
        var config = BuildConfig(
            ("MAILER_TENANTS_PATH", tenantsPath),
            ("MAIL_SERVICE_TOKEN", TokenCanary));

        var (exit, stdout, stderr) = await RunAsync(config, ["setup", "inspect-effective", "--format", "json"]);

        Assert.Equal(SetupInspectEffectiveCommand.SuccessExitCode, exit);
        Assert.True(string.IsNullOrWhiteSpace(stderr));
        var result = Deserialize(stdout);
        Assert.Equal(1, result.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(result.MailerVersion));
        Assert.False(result.Managed);
        Assert.Null(result.Recorded);
        Assert.Equal(SetupInspectIntegrityResult.NotManaged, result.MountAttestation.Result);
        Assert.Equal(SetupInspectIntegrityResult.NotManaged, result.BundleIntegrity.Result);
        Assert.Equal("mailpit", result.Effective.ProviderSummary);
        Assert.False(result.Effective.LiveSendingEnabled);
        Assert.Equal(SetupInspectCredentialStatus.NotApplicable, result.Effective.CredentialStatus);
        Assert.Equal(SetupInspectSourceIds.ContainerTenants, result.TenantConfigurationSource);
        AssertNoCanaries(stdout, stderr);
    }

    [Fact]
    public async Task Malformed_metadata_is_invalid_metadata_and_does_not_leak()
    {
        using var dir = new TempDir();
        var tenantsPath = WriteTenants(dir.Path, SetupTestFixtures.LocalMailpitTenants());
        var metadataPath = Path.Combine(dir.Path, "recorded.json");
        await File.WriteAllTextAsync(metadataPath, "{ not-json " + SecretCanary);

        var config = BuildConfig(
            ("MAILER_TENANTS_PATH", tenantsPath),
            ("MAILER_SETUP_RECORDED_METADATA_PATH", metadataPath),
            ("MAIL_SERVICE_TOKEN", TokenCanary));

        var (exit, stdout, stderr) = await RunAsync(config, ["setup", "inspect-effective", "--format", "json"]);

        Assert.Equal(SetupInspectEffectiveCommand.InspectionIssueExitCode, exit);
        var result = Deserialize(stdout);
        Assert.False(result.Managed);
        Assert.Equal(SetupInspectIntegrityResult.InvalidMetadata, result.BundleIntegrity.Result);
        Assert.Equal(SetupInspectReason.MetadataMalformed, result.Reason);
        AssertNoCanaries(stdout, stderr, SecretCanary);
    }

    [Fact]
    public async Task Managed_with_matching_mount_attestation_never_claims_final_matched()
    {
        using var dir = new TempDir();
        var bundle = await GenerateAcsBundleAsync(dir.Path);
        var tenantsPath = Path.Combine(bundle.BundleRoot, "config", "tenants.json");
        var acsPath = Path.Combine(bundle.BundleRoot, "secrets", "acs_connection_string");
        var metadataPath = Path.Combine(bundle.BundleRoot, "metadata", "recorded.json");
        var verifierPath = Path.Combine(dir.Path, "verifier.json");
        using var stagedRecorded = StageContainerRecordedMetadata(metadataPath);

        WriteVerifier(
            verifierPath,
            bundle.BundleId,
            expiresAtUnix: DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
            AcsRequiredMembers(await File.ReadAllBytesAsync(acsPath), TokenCanary));

        var config = BuildConfigFromCompose(
            bundle.ComposeEnv,
            ("MAILER_TENANTS_PATH", tenantsPath),
            // Keep compose MAILER_SETUP_RECORDED_METADATA_PATH for fingerprint sameness.
            ("ACS_CONNECTION_STRING_FILE", acsPath),
            ("MAILER_SETUP_MOUNT_VERIFIER_PATH", verifierPath),
            ("MAIL_SERVICE_TOKEN_STAGING", TokenCanary));

        var (exit, stdout, stderr) = await RunAsync(config, ["setup", "inspect-effective", "--format", "json"]);

        Assert.Equal(SetupInspectEffectiveCommand.SuccessExitCode, exit);
        var result = Deserialize(stdout);
        Assert.True(result.Managed);
        Assert.Equal(bundle.BundleId, result.Recorded!.SetupBundleId);
        Assert.True(result.Effective.FingerprintsMatchRecorded);
        Assert.Equal(SetupInspectIntegrityResult.Matched, result.MountAttestation.Result);
        Assert.Equal(SetupInspectIntegrityResult.NotVerified, result.BundleIntegrity.Result);
        Assert.Equal(SetupInspectReason.HostAtRestPending, result.BundleIntegrity.Reason);
        Assert.Equal("provisional", result.BundleIntegrity.Scope);
        Assert.Equal("acs", result.Effective.ProviderSummary);
        Assert.Equal(SetupInspectCredentialStatus.Loaded, result.Effective.CredentialStatus);
        Assert.Equal(SetupInspectSourceIds.ContainerAcsFile, result.CredentialSource);
        Assert.False(string.IsNullOrWhiteSpace(result.Recorded!.ConfigurationFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(result.Effective.ConfigurationFingerprint));
        Assert.NotNull(result.Effective.FingerprintsMatchRecorded);
        AssertNoCanaries(stdout, stderr, SecretCanary, TokenCanary, PathCanary);
        Assert.DoesNotContain("sessionKey", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expectedMac", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Secret_swap_is_mount_mismatch_not_matched()
    {
        using var dir = new TempDir();
        var bundle = await GenerateAcsBundleAsync(dir.Path);
        var tenantsPath = Path.Combine(bundle.BundleRoot, "config", "tenants.json");
        var acsPath = Path.Combine(bundle.BundleRoot, "secrets", "acs_connection_string");
        var metadataPath = Path.Combine(bundle.BundleRoot, "metadata", "recorded.json");
        var verifierPath = Path.Combine(dir.Path, "verifier.json");
        var original = await File.ReadAllBytesAsync(acsPath);
        using var stagedRecorded = StageContainerRecordedMetadata(metadataPath);

        WriteVerifier(
            verifierPath,
            bundle.BundleId,
            expiresAtUnix: DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
            AcsRequiredMembers(original, TokenCanary));

        await File.WriteAllTextAsync(acsPath, "endpoint=https://swapped.example/;accesskey=SWAPPED_SECRET");

        var config = BuildConfigFromCompose(
            bundle.ComposeEnv,
            ("MAILER_TENANTS_PATH", tenantsPath),
            // Keep compose MAILER_SETUP_RECORDED_METADATA_PATH for fingerprint sameness.
            ("ACS_CONNECTION_STRING_FILE", acsPath),
            ("MAILER_SETUP_MOUNT_VERIFIER_PATH", verifierPath),
            ("MAIL_SERVICE_TOKEN_STAGING", TokenCanary));

        var (exit, stdout, stderr) = await RunAsync(config, ["setup", "inspect-effective", "--format", "json"]);

        Assert.Equal(SetupInspectEffectiveCommand.InspectionIssueExitCode, exit);
        var result = Deserialize(stdout);
        Assert.Equal(SetupInspectIntegrityResult.Mismatch, result.MountAttestation.Result);
        Assert.Equal(SetupInspectReason.MountMismatch, result.MountAttestation.Reason);
        Assert.Equal(SetupInspectIntegrityResult.Mismatch, result.BundleIntegrity.Result);
        AssertNoCanaries(stdout, stderr, "SWAPPED_SECRET", SecretCanary, TokenCanary);
    }

    [Fact]
    public async Task Wrong_bundle_secret_and_expired_verifier_are_not_matched()
    {
        using var dir = new TempDir();
        var bundle = await GenerateAcsBundleAsync(dir.Path);
        var tenantsPath = Path.Combine(bundle.BundleRoot, "config", "tenants.json");
        var acsPath = Path.Combine(bundle.BundleRoot, "secrets", "acs_connection_string");
        var metadataPath = Path.Combine(bundle.BundleRoot, "metadata", "recorded.json");
        var foreignBytes = Encoding.UTF8.GetBytes("endpoint=https://other-bundle.example/;accesskey=OTHER");
        var verifierPath = Path.Combine(dir.Path, "verifier.json");
        using var stagedRecorded = StageContainerRecordedMetadata(metadataPath);

        WriteVerifier(
            verifierPath,
            "other-bundle-id",
            expiresAtUnix: DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
            (SetupMountAttestation.AcsConnectionStringMemberId, foreignBytes));

        var config = BuildConfigFromCompose(
            bundle.ComposeEnv,
            ("MAILER_TENANTS_PATH", tenantsPath),
            // Keep compose MAILER_SETUP_RECORDED_METADATA_PATH for fingerprint sameness.
            ("ACS_CONNECTION_STRING_FILE", acsPath),
            ("MAILER_SETUP_MOUNT_VERIFIER_PATH", verifierPath),
            ("MAIL_SERVICE_TOKEN_STAGING", TokenCanary));

        var (_, stdout, stderr) = await RunAsync(config, ["setup", "inspect-effective", "--format", "json"]);
        var result = Deserialize(stdout);
        Assert.Equal(SetupInspectIntegrityResult.Mismatch, result.MountAttestation.Result);
        Assert.Equal(SetupInspectReason.VerifierBundleMismatch, result.MountAttestation.Reason);
        Assert.NotEqual(SetupInspectIntegrityResult.Matched, result.BundleIntegrity.Result);
        AssertNoCanaries(stdout, stderr, "OTHER", SecretCanary);

        WriteVerifier(
            verifierPath,
            bundle.BundleId,
            expiresAtUnix: DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
            (SetupMountAttestation.AcsConnectionStringMemberId, await File.ReadAllBytesAsync(acsPath)));

        int expiredExit;
        (expiredExit, stdout, stderr) = await RunAsync(config, ["setup", "inspect-effective", "--format", "json"]);
        Assert.Equal(SetupInspectEffectiveCommand.InspectionIncompleteExitCode, expiredExit);
        result = Deserialize(stdout);
        Assert.Equal(SetupInspectIntegrityResult.NotVerified, result.MountAttestation.Result);
        Assert.Equal(SetupInspectReason.VerifierExpired, result.MountAttestation.Reason);
        Assert.Equal(SetupInspectIntegrityResult.NotVerified, result.BundleIntegrity.Result);
        AssertNoCanaries(stdout, stderr, SecretCanary);
    }

    [Fact]
    public async Task Missing_acs_credential_file_is_credential_missing_issue()
    {
        using var dir = new TempDir();
        var bundle = await GenerateAcsBundleAsync(dir.Path);
        var tenantsPath = Path.Combine(bundle.BundleRoot, "config", "tenants.json");
        var acsPath = Path.Combine(bundle.BundleRoot, "secrets", "acs_connection_string");
        var metadataPath = Path.Combine(bundle.BundleRoot, "metadata", "recorded.json");
        var verifierPath = Path.Combine(dir.Path, "verifier.json");
        var original = await File.ReadAllBytesAsync(acsPath);
        using var stagedRecorded = StageContainerRecordedMetadata(metadataPath);

        WriteVerifier(
            verifierPath,
            bundle.BundleId,
            expiresAtUnix: DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
            AcsRequiredMembers(original, TokenCanary));

        var missingPath = Path.Combine(dir.Path, "missing-acs");
        var config = BuildConfigFromCompose(
            bundle.ComposeEnv,
            ("MAILER_TENANTS_PATH", tenantsPath),
            ("ACS_CONNECTION_STRING_FILE", missingPath),
            ("MAILER_SETUP_MOUNT_VERIFIER_PATH", verifierPath),
            ("MAIL_SERVICE_TOKEN_STAGING", TokenCanary));

        var (exit, stdout, stderr) = await RunAsync(config, ["setup", "inspect-effective", "--format", "json"]);
        Assert.Equal(SetupInspectEffectiveCommand.InspectionIssueExitCode, exit);
        var result = Deserialize(stdout);
        Assert.Equal(SetupInspectReason.CredentialMissing, result.Reason);
        Assert.Equal(SetupInspectCredentialStatus.Missing, result.Effective.CredentialStatus);
        Assert.Equal(SetupInspectIntegrityResult.NotVerified, result.MountAttestation.Result);
        AssertNoCanaries(stdout, stderr, SecretCanary, TokenCanary);
    }

    [Fact]
    public async Task Stdout_is_json_only_and_unknown_args_are_not_echoed()
    {
        using var dir = new TempDir();
        var tenantsPath = WriteTenants(dir.Path, SetupTestFixtures.LocalMailpitTenants());
        var config = BuildConfig(("MAILER_TENANTS_PATH", tenantsPath));

        var (exit, stdout, stderr) = await RunAsync(
            config,
            ["setup", "inspect-effective", "--format", "json", "--evil", PathCanary]);

        Assert.Equal(SetupInspectEffectiveCommand.UsageErrorExitCode, exit);
        Assert.True(string.IsNullOrWhiteSpace(stdout));
        Assert.Contains("Unknown argument", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(PathCanary, stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("--evil", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Incomplete_verifier_member_set_is_not_matched()
    {
        using var dir = new TempDir();
        var bundle = await GenerateAcsBundleAsync(dir.Path);
        var tenantsPath = Path.Combine(bundle.BundleRoot, "config", "tenants.json");
        var acsPath = Path.Combine(bundle.BundleRoot, "secrets", "acs_connection_string");
        var metadataPath = Path.Combine(bundle.BundleRoot, "metadata", "recorded.json");
        var verifierPath = Path.Combine(dir.Path, "verifier.json");

        using var stagedRecorded = StageContainerRecordedMetadata(metadataPath);

        // ACS only — omits required env:MAIL_SERVICE_TOKEN_STAGING (Agent B M-01).
        WriteVerifier(
            verifierPath,
            bundle.BundleId,
            expiresAtUnix: DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
            (SetupMountAttestation.AcsConnectionStringMemberId, await File.ReadAllBytesAsync(acsPath)));

        var config = BuildConfigFromCompose(
            bundle.ComposeEnv,
            ("MAILER_TENANTS_PATH", tenantsPath),
            // Keep compose MAILER_SETUP_RECORDED_METADATA_PATH for fingerprint sameness.
            ("ACS_CONNECTION_STRING_FILE", acsPath),
            ("MAILER_SETUP_MOUNT_VERIFIER_PATH", verifierPath),
            ("MAIL_SERVICE_TOKEN_STAGING", TokenCanary));

        var (exit, stdout, stderr) = await RunAsync(config, ["setup", "inspect-effective", "--format", "json"]);
        Assert.Equal(SetupInspectEffectiveCommand.InspectionIssueExitCode, exit);
        var result = Deserialize(stdout);
        Assert.Equal(SetupInspectIntegrityResult.Mismatch, result.MountAttestation.Result);
        Assert.Equal(SetupInspectReason.VerifierMemberSetMismatch, result.MountAttestation.Reason);
        Assert.NotEqual(SetupInspectIntegrityResult.Matched, result.BundleIntegrity.Result);
        AssertNoCanaries(stdout, stderr, SecretCanary, TokenCanary);
    }

    [Fact]
    public async Task Missing_tenant_token_is_credential_issue_like_runtime()
    {
        using var dir = new TempDir();
        var tenantsPath = WriteTenants(dir.Path, SetupTestFixtures.LocalMailpitTenants());
        var config = BuildConfig(("MAILER_TENANTS_PATH", tenantsPath));

        var (exit, stdout, stderr) = await RunAsync(config, ["setup", "inspect-effective", "--format", "json"]);
        Assert.Equal(SetupInspectEffectiveCommand.InspectionIssueExitCode, exit);
        var result = Deserialize(stdout);
        Assert.Equal(SetupInspectReason.CredentialMissing, result.Reason);
        Assert.False(result.Managed);
        AssertNoCanaries(stdout, stderr);
    }

    [Fact]
    public async Task Missing_webhook_secret_is_credential_issue_like_runtime()
    {
        using var dir = new TempDir();
        var tenants = SetupTestFixtures.LocalMailpitTenants();
        tenants = new MailerTenantsFile
        {
            Version = tenants.Version,
            Environment = tenants.Environment,
            Tenants =
            [
                new MailerTenant
                {
                    TenantId = tenants.Tenants[0].TenantId,
                    Name = tenants.Tenants[0].Name,
                    SourceServices = tenants.Tenants[0].SourceServices,
                    DefaultFrom = tenants.Tenants[0].DefaultFrom,
                    TokenEnv = tenants.Tenants[0].TokenEnv,
                    Provider = tenants.Tenants[0].Provider,
                    LiveSending = tenants.Tenants[0].LiveSending,
                    Retry = tenants.Tenants[0].Retry,
                    Webhook = new MailerWebhookConfig
                    {
                        Url = "https://example.com/webhook",
                        SecretEnv = "WEBHOOK_SIGNING_SECRET",
                    },
                },
            ],
        };
        var tenantsPath = WriteTenants(dir.Path, tenants);
        var config = BuildConfig(
            ("MAILER_TENANTS_PATH", tenantsPath),
            ("MAIL_SERVICE_TOKEN", TokenCanary));

        var (exit, stdout, stderr) = await RunAsync(config, ["setup", "inspect-effective", "--format", "json"]);
        Assert.Equal(SetupInspectEffectiveCommand.InspectionIssueExitCode, exit);
        var result = Deserialize(stdout);
        Assert.Equal(SetupInspectReason.CredentialMissing, result.Reason);
        AssertNoCanaries(stdout, stderr, TokenCanary);
    }

    [Fact]
    public async Task Unknown_provider_is_config_conflict_like_runtime()
    {
        using var dir = new TempDir();
        var tenantsPath = WriteTenants(dir.Path, SetupTestFixtures.LocalMailpitTenants());
        var config = BuildConfig(
            ("MAILER_TENANTS_PATH", tenantsPath),
            ("MAIL_SERVICE_TOKEN", TokenCanary),
            ("MAILER_PROVIDER", "acs-secret-value"));

        var (exit, stdout, stderr) = await RunAsync(config, ["setup", "inspect-effective", "--format", "json"]);
        Assert.Equal(SetupInspectEffectiveCommand.InspectionIssueExitCode, exit);
        var result = Deserialize(stdout);
        Assert.Equal(SetupInspectReason.ConfigConflict, result.Reason);
        Assert.DoesNotContain("acs-secret-value", stdout, StringComparison.Ordinal);
        AssertNoCanaries(stdout, stderr, TokenCanary);
    }

    [Fact]
    public async Task Mailpit_port_conflict_is_config_conflict_like_runtime()
    {
        using var dir = new TempDir();
        var tenantsPath = WriteTenants(dir.Path, SetupTestFixtures.LocalMailpitTenants());
        var config = BuildConfig(
            ("MAILER_TENANTS_PATH", tenantsPath),
            ("MAIL_SERVICE_TOKEN", TokenCanary),
            ("MAILPIT_SMTP_PORT", "not-a-port"));

        var (exit, stdout, stderr) = await RunAsync(config, ["setup", "inspect-effective", "--format", "json"]);
        Assert.Equal(SetupInspectEffectiveCommand.InspectionIssueExitCode, exit);
        var result = Deserialize(stdout);
        Assert.Equal(SetupInspectReason.ConfigConflict, result.Reason);
        AssertNoCanaries(stdout, stderr, TokenCanary);
    }

    [Fact]
    public async Task Verifier_missing_exits_incomplete()
    {
        using var dir = new TempDir();
        var bundle = await GenerateAcsBundleAsync(dir.Path);
        var tenantsPath = Path.Combine(bundle.BundleRoot, "config", "tenants.json");
        var acsPath = Path.Combine(bundle.BundleRoot, "secrets", "acs_connection_string");
        var metadataPath = Path.Combine(bundle.BundleRoot, "metadata", "recorded.json");
        using var stagedRecorded = StageContainerRecordedMetadata(metadataPath);

        var config = BuildConfigFromCompose(
            bundle.ComposeEnv,
            ("MAILER_TENANTS_PATH", tenantsPath),
            // Keep compose MAILER_SETUP_RECORDED_METADATA_PATH for fingerprint sameness.
            ("ACS_CONNECTION_STRING_FILE", acsPath),
            ("MAIL_SERVICE_TOKEN_STAGING", TokenCanary));

        var (exit, stdout, stderr) = await RunAsync(config, ["setup", "inspect-effective", "--format", "json"]);
        Assert.Equal(SetupInspectEffectiveCommand.InspectionIncompleteExitCode, exit);
        var result = Deserialize(stdout);
        Assert.Equal(SetupInspectIntegrityResult.NotVerified, result.MountAttestation.Result);
        Assert.Equal(SetupInspectReason.VerifierMissing, result.MountAttestation.Reason);
        AssertNoCanaries(stdout, stderr, SecretCanary, TokenCanary);
    }

    [Fact]
    public async Task Verifier_expired_exits_incomplete()
    {
        using var dir = new TempDir();
        var bundle = await GenerateAcsBundleAsync(dir.Path);
        var tenantsPath = Path.Combine(bundle.BundleRoot, "config", "tenants.json");
        var acsPath = Path.Combine(bundle.BundleRoot, "secrets", "acs_connection_string");
        var metadataPath = Path.Combine(bundle.BundleRoot, "metadata", "recorded.json");
        var verifierPath = Path.Combine(dir.Path, "verifier.json");
        using var stagedRecorded = StageContainerRecordedMetadata(metadataPath);

        WriteVerifier(
            verifierPath,
            bundle.BundleId,
            expiresAtUnix: DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
            AcsRequiredMembers(await File.ReadAllBytesAsync(acsPath), TokenCanary));

        var config = BuildConfigFromCompose(
            bundle.ComposeEnv,
            ("MAILER_TENANTS_PATH", tenantsPath),
            // Keep compose MAILER_SETUP_RECORDED_METADATA_PATH for fingerprint sameness.
            ("ACS_CONNECTION_STRING_FILE", acsPath),
            ("MAILER_SETUP_MOUNT_VERIFIER_PATH", verifierPath),
            ("MAIL_SERVICE_TOKEN_STAGING", TokenCanary));

        var (exit, stdout, stderr) = await RunAsync(config, ["setup", "inspect-effective", "--format", "json"]);
        Assert.Equal(SetupInspectEffectiveCommand.InspectionIncompleteExitCode, exit);
        var result = Deserialize(stdout);
        Assert.Equal(SetupInspectIntegrityResult.NotVerified, result.MountAttestation.Result);
        Assert.Equal(SetupInspectReason.VerifierExpired, result.MountAttestation.Reason);
        AssertNoCanaries(stdout, stderr, SecretCanary, TokenCanary);
    }

    [Fact]
    public async Task Recorded_metadata_is_not_used_as_effective_provider()
    {
        using var dir = new TempDir();
        var tenants = SetupTestFixtures.LocalMailpitTenants();
        var tenantsPath = WriteTenants(dir.Path, tenants);
        var metadataPath = Path.Combine(dir.Path, "recorded.json");
        var recorded = new SetupRecordedMetadata
        {
            SchemaVersion = 1,
            BundleId = "20260728120000-abcdef12",
            ConfigurationFingerprint = "sha256:" + new string('a', 64),
            Mode = "production-acs",
            CreatedAt = "2026-07-28T00:00:00Z",
        };
        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(recorded, SetupJsonContext.Default.SetupRecordedMetadata));

        var config = BuildConfig(
            ("MAILER_TENANTS_PATH", tenantsPath),
            ("MAILER_SETUP_RECORDED_METADATA_PATH", metadataPath),
            ("MAILER_PROVIDER", "mailpit"),
            ("MAIL_SERVICE_TOKEN", TokenCanary));

        var (exit, stdout, _) = await RunAsync(config, ["setup", "inspect-effective", "--format", "json"]);
        Assert.Equal(SetupInspectEffectiveCommand.InspectionIssueExitCode, exit);
        var result = Deserialize(stdout);
        Assert.True(result.Managed);
        Assert.Equal("production-acs", result.Recorded!.Mode);
        Assert.Equal("mailpit", result.Effective.ProviderSummary);
        Assert.False(result.Effective.LiveSendingEnabled);
        Assert.False(result.Effective.FingerprintsMatchRecorded);
        Assert.Equal(SetupInspectReason.FingerprintMismatch, result.Reason);
    }


    [Fact]
    public void Effective_fingerprint_matches_recorded_when_public_inputs_are_identical()
    {
        var tenants = SetupTestFixtures.AcsStagingTenants();
        var platformSender = SetupRequestValidator.BuildPlatformSender(new SetupPlatformSenderInput
        {
            Environment = "staging",
            Email = "platform@example.com",
            DisplayName = "Platform Sender",
        });
        var compose = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["MAILER_PROVIDER"] = string.Empty,
            ["MAILER_IMAGE_TAG"] = "test-synthetic-image-tag",
            ["MAILER_SETUP_RECORDED_METADATA_PATH"] = SetupBundleLayout.ContainerRecordedMetadataPath,
            ["MAILER_TENANTS_HOST_PATH"] = "bundles/<bundle-id>/config/tenants.json",
            ["MAILER_TENANTS_CONTAINER_PATH"] = SetupBundleLayout.ContainerTenantsPath,
        };
        var canonical = SetupCanonicalPayload.Build(
            SetupMode.StagingNoSend,
            tenants,
            compose,
            platformSender,
            adminBootstrapRequested: false);
        var expected = SetupCanonicalPayload.FingerprintSha256(canonical);
        var actualCanonical = SetupCanonicalPayload.BuildFromWireMode(
            "staging-no-send",
            tenants,
            compose,
            platformSender,
            adminBootstrapRequested: false);
        Assert.Equal(expected, SetupCanonicalPayload.FingerprintSha256(actualCanonical));
    }
    [Fact]
    public void Usage_requires_format_json()
    {
        Assert.False(SetupInspectEffectiveCommand.TryParseArguments(
            ["setup", "inspect-effective"],
            out var error));
        Assert.Contains("--format", error, StringComparison.Ordinal);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        IConfiguration configuration,
        string[] args)
    {
        await using var stdout = new StringWriter();
        await using var stderr = new StringWriter();
        if (!SetupInspectEffectiveCommand.TryParseArguments(args, out var usageError))
        {
            await stderr.WriteLineAsync(usageError ?? "Invalid setup inspect-effective arguments.");
            await stderr.WriteLineAsync("Usage: setup inspect-effective --format json");
            return (SetupInspectEffectiveCommand.UsageErrorExitCode, stdout.ToString(), stderr.ToString());
        }

        var exit = await SetupInspectEffectiveCommand.ExecuteAsync(configuration, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static SetupInspectEffectiveResult Deserialize(string stdout)
    {
        var trimmed = stdout.Trim();
        Assert.StartsWith("{", trimmed);
        Assert.EndsWith("}", trimmed);
        using var _ = JsonDocument.Parse(trimmed);
        return JsonSerializer.Deserialize(trimmed, SetupInspectJsonContext.Default.SetupInspectEffectiveResult)
            ?? throw new InvalidOperationException("Failed to deserialize inspect result.");
    }

    private static IConfiguration BuildConfig(params (string Key, string Value)[] pairs)
    {
        var dict = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return new ConfigurationBuilder().AddInMemoryCollection(dict!).Build();
    }

    private static IConfiguration BuildConfigFromCompose(
        IReadOnlyDictionary<string, string> composeEnv,
        params (string Key, string Value)[] pairs)
    {
        var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in composeEnv)
        {
            dict[pair.Key] = pair.Value;
        }

        foreach (var pair in pairs)
        {
            dict[pair.Key] = pair.Value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static StagedRecordedMetadata StageContainerRecordedMetadata(string sourcePath) =>
        new(sourcePath);

    private sealed class StagedRecordedMetadata : IDisposable
    {
        private readonly string _dest;

        public StagedRecordedMetadata(string sourcePath)
        {
            _dest = Path.GetFullPath(SetupBundleLayout.ContainerRecordedMetadataPath);
            Directory.CreateDirectory(Path.GetDirectoryName(_dest)!);
            File.Copy(sourcePath, _dest, overwrite: true);
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(_dest))
                {
                    File.Delete(_dest);
                }
            }
            catch
            {
                // best-effort cleanup for host-side container path staging
            }
        }
    }

    private static string WriteTenants(string root, MailerTenantsFile tenants)
    {
        var path = Path.Combine(root, "tenants.json");
        File.WriteAllText(path, JsonSerializer.Serialize(tenants, SetupJsonContext.Default.MailerTenantsFile));
        return path;
    }

    private static void WriteVerifier(
        string path,
        string bundleId,
        long expiresAtUnix,
        params (string MemberId, byte[] Content)[] members)
    {
        var sessionKey = SetupMountAttestation.CreateSessionKey();
        var sessionNonce = SetupMountAttestation.CreateSessionNonce();
        try
        {
            var doc = new SetupMountVerifierDocument
            {
                SchemaVersion = 1,
                BundleId = bundleId,
                SessionNonce = Convert.ToBase64String(sessionNonce),
                SessionKey = Convert.ToBase64String(sessionKey),
                ExpiresAtUnix = expiresAtUnix,
                Members = members.Select(m => new SetupMountVerifierMember
                {
                    MemberId = m.MemberId,
                    ExpectedMac = Convert.ToBase64String(
                        SetupMountAttestation.ComputeMac(sessionKey, sessionNonce, m.MemberId, m.Content)),
                }).ToArray(),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(doc, SetupInspectJsonContext.Default.SetupMountVerifierDocument));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionKey);
            CryptographicOperations.ZeroMemory(sessionNonce);
        }
    }

    private static (string MemberId, byte[] Content) TokenMember(string envKey, string token) =>
        (SetupMountAttestation.EnvMemberId(envKey), Encoding.UTF8.GetBytes(token));

    private static (string MemberId, byte[] Content)[] AcsRequiredMembers(byte[] acsBytes, string token) =>
    [
        (SetupMountAttestation.AcsConnectionStringMemberId, acsBytes),
        TokenMember("MAIL_SERVICE_TOKEN_STAGING", token),
    ];

    private static async Task<GeneratedBundle> GenerateAcsBundleAsync(string root)
    {
        var managedRoot = Path.Combine(root, "managed");
        Directory.CreateDirectory(managedRoot);
        var request = SetupTestFixtures.StagingAcsRequest(managedRoot, dryRun: false);
        // Override ACS secret with canary for leak tests.
        request = new SetupRequest
        {
            Mode = request.Mode,
            ManagedRootPath = request.ManagedRootPath,
            DryRun = false,
            Tenants = request.Tenants,
            TokenSecrets = request.TokenSecrets,
            WebhookSecrets = request.WebhookSecrets,
            MetricsBearerToken = request.MetricsBearerToken,
            AcsConnectionString = SecretCanary,
            PlatformSender = request.PlatformSender,
            PublicEnvOverrides = request.PublicEnvOverrides,
            Admin = request.Admin,
            ImageRepository = request.ImageRepository,
            ImageTag = request.ImageTag,
            RuntimeFileOwnership = request.RuntimeFileOwnership,
        };

        var core = new SetupCore(new HostSetupFileSystem());
        var result = core.GenerateBundle(request);
        Assert.Equal(SetupResultCode.Succeeded, result.Code);
        Assert.False(string.IsNullOrWhiteSpace(result.BundleId));

        var bundleRoot = SetupBundleLayout.BundleRoot(managedRoot, result.BundleId!);
        var composePath = Path.Combine(bundleRoot, "env", "compose.env");
        var composeEnv = ParseEnvFile(await File.ReadAllTextAsync(composePath));
        return new GeneratedBundle(result.BundleId!, bundleRoot, composeEnv);
    }

    private static Dictionary<string, string> ParseEnvFile(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = line.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            var key = line[..idx];
            var value = line[(idx + 1)..];
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1]
                    .Replace("\\\\", "\\", StringComparison.Ordinal)
                    .Replace("\\\"", "\"", StringComparison.Ordinal)
                    .Replace("$$", "$", StringComparison.Ordinal);
            }

            map[key] = value;
        }

        return map;
    }

    private static void AssertNoCanaries(string stdout, string stderr, params string[] extra)
    {
        foreach (var canary in extra.Concat(
                     [SecretCanary, TokenCanary, PathCanary, "accesskey=", "HMAC", "sessionKey"]))
        {
            Assert.DoesNotContain(canary, stdout, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(canary, stderr, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "amane-inspect-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best-effort
            }
        }
    }

    private sealed record GeneratedBundle(
        string BundleId,
        string BundleRoot,
        IReadOnlyDictionary<string, string> ComposeEnv);
}
