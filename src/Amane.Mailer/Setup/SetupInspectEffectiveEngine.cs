using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amane.Mailer.Configuration;
using Amane.Mailer.Json;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Setup;

/// <summary>
/// Resolves effective non-secret configuration and container mount attestation using the same
/// configuration load/validation path as runtime (ADR 0021 D-05).
/// </summary>
public static partial class SetupInspectEffectiveEngine
{
    public const string MountVerifierPathEnv = "MAILER_SETUP_MOUNT_VERIFIER_PATH";
    public const string RecordedMetadataPathEnv = "MAILER_SETUP_RECORDED_METADATA_PATH";

    [GeneratedRegex("^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintRegex();

    [GeneratedRegex("^[0-9]{14}-[a-f0-9]{8}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BundleIdRegex();

    public static SetupInspectEffectiveResult Inspect(
        IConfiguration configuration,
        string environmentName,
        TimeProvider? timeProvider = null)
    {
        timeProvider ??= TimeProvider.System;
        var utcNow = timeProvider.GetUtcNow();
        var mailerVersion = ResolveMailerVersion();

        var recordedLoad = TryLoadRecorded(configuration);
        if (recordedLoad.Kind is RecordedLoadKind.Malformed or RecordedLoadKind.UnsupportedSchema)
        {
            var invalidReason = recordedLoad.Kind == RecordedLoadKind.UnsupportedSchema
                ? SetupInspectReason.UnsupportedSchemaVersion
                : SetupInspectReason.MetadataMalformed;
            return TerminalInvalidMetadata(mailerVersion, invalidReason);
        }

        var managed = recordedLoad.Kind == RecordedLoadKind.Present;
        SetupInspectRecordedSummary? recordedSummary = null;
        if (managed && recordedLoad.Metadata is not null)
        {
            recordedSummary = new SetupInspectRecordedSummary
            {
                SetupBundleId = recordedLoad.Metadata.BundleId,
                ConfigurationFingerprint = recordedLoad.Metadata.ConfigurationFingerprint,
                Mode = recordedLoad.Metadata.Mode,
                SchemaVersion = recordedLoad.Metadata.SchemaVersion,
            };
        }

        var load = MailerConfigurationSnapshot.TryLoad(configuration, environmentName);
        if (!load.Succeeded || load.Snapshot is null)
        {
            return TerminalConfigFailure(mailerVersion, managed, recordedSummary, load.FailureKind);
        }

        var snapshot = load.Snapshot;
        var options = snapshot.Options;
        var tenantsFile = snapshot.TenantsFile;
        var tenants = snapshot.Registry.ListTenants();

        var providerSummary = SummarizeProvider(options, tenants);
        if (providerSummary is null)
        {
            return TerminalConfigFailure(
                mailerVersion,
                managed,
                recordedSummary,
                MailerConfigurationLoadFailureKind.ProviderInvalid);
        }

        var liveSending = tenants.Any(t => t.LiveSending);
        var (credentialStatus, credentialSource) = ResolveCredentialStatus(options, tenants, configuration);
        if (credentialStatus is SetupInspectCredentialStatus.Missing or SetupInspectCredentialStatus.Invalid)
        {
            var credReason = credentialStatus == SetupInspectCredentialStatus.Invalid
                ? SetupInspectReason.CredentialInvalid
                : SetupInspectReason.CredentialMissing;
            var credMount = managed
                ? Attestation(SetupInspectIntegrityResult.NotVerified, credReason)
                : Attestation(SetupInspectIntegrityResult.NotManaged, SetupInspectReason.MetadataMissing);
            return BuildResult(
                mailerVersion,
                managed,
                recordedSummary,
                new SetupInspectEffectiveSummary
                {
                    ProviderSummary = providerSummary,
                    LiveSendingEnabled = liveSending,
                    CredentialStatus = credentialStatus,
                },
                credMount,
                DeriveProvisionalIntegrity(managed, credMount),
                SetupInspectSourceIds.ContainerTenants,
                credentialSource,
                credReason);
        }

        string? effectiveFingerprint = null;
        bool? fingerprintsMatch = null;
        try
        {
            effectiveFingerprint = ComputeEffectiveFingerprint(
                configuration,
                tenantsFile,
                snapshot.TenantsPath,
                recordedLoad.Metadata);
            if (recordedSummary is not null && effectiveFingerprint is not null)
            {
                fingerprintsMatch = string.Equals(
                    recordedSummary.ConfigurationFingerprint,
                    effectiveFingerprint,
                    StringComparison.Ordinal);
            }
        }
        catch
        {
            effectiveFingerprint = null;
            fingerprintsMatch = null;
        }

        var effective = new SetupInspectEffectiveSummary
        {
            ConfigurationFingerprint = effectiveFingerprint,
            ProviderSummary = providerSummary,
            LiveSendingEnabled = liveSending,
            CredentialStatus = credentialStatus,
            FingerprintsMatchRecorded = fingerprintsMatch,
        };

        var requiredMembers = SetupMountAttestation.DeriveRequiredMemberIds(options, tenants, configuration);
        var mount = ResolveMountAttestation(
            configuration,
            managed,
            recordedLoad.Metadata,
            requiredMembers,
            utcNow);

        var integrity = DeriveProvisionalIntegrity(managed, mount);
        var reason = integrity.Reason
            ?? mount.Reason
            ?? (managed ? null : SetupInspectReason.MetadataMissing);

        // Prefer fingerprint mismatch over incomplete mount reasons when both apply.
        if (fingerprintsMatch == false)
        {
            reason = SetupInspectReason.FingerprintMismatch;
        }

        return BuildResult(
            mailerVersion,
            managed,
            recordedSummary,
            effective,
            mount,
            integrity,
            SetupInspectSourceIds.ContainerTenants,
            credentialSource,
            reason);
    }

    private static SetupInspectEffectiveResult TerminalInvalidMetadata(string mailerVersion, string reason) =>
        BuildResult(
            mailerVersion,
            managed: false,
            recorded: null,
            EmptyEffective(),
            Attestation(SetupInspectIntegrityResult.InvalidMetadata, reason),
            Attestation(SetupInspectIntegrityResult.InvalidMetadata, reason, scope: "provisional"),
            SetupInspectSourceIds.NotApplicable,
            SetupInspectSourceIds.NotApplicable,
            reason);

    private static SetupInspectEffectiveResult TerminalConfigFailure(
        string mailerVersion,
        bool managed,
        SetupInspectRecordedSummary? recorded,
        MailerConfigurationLoadFailureKind failureKind)
    {
        var (reason, credentialStatus) = failureKind switch
        {
            MailerConfigurationLoadFailureKind.TenantsMissing =>
                (SetupInspectReason.TenantsMissing, SetupInspectCredentialStatus.NotApplicable),
            MailerConfigurationLoadFailureKind.TenantsInvalid =>
                (SetupInspectReason.TenantsInvalid, SetupInspectCredentialStatus.NotApplicable),
            MailerConfigurationLoadFailureKind.TokenMissing =>
                (SetupInspectReason.CredentialMissing, SetupInspectCredentialStatus.Missing),
            MailerConfigurationLoadFailureKind.WebhookSecretMissing =>
                (SetupInspectReason.CredentialMissing, SetupInspectCredentialStatus.Missing),
            MailerConfigurationLoadFailureKind.AcsCredentialMissing =>
                (SetupInspectReason.CredentialMissing, SetupInspectCredentialStatus.Missing),
            MailerConfigurationLoadFailureKind.ProviderInvalid =>
                (SetupInspectReason.ProviderInvalid, SetupInspectCredentialStatus.NotApplicable),
            MailerConfigurationLoadFailureKind.MailpitInvalid =>
                (SetupInspectReason.MailpitInvalid, SetupInspectCredentialStatus.NotApplicable),
            // None (and any future unclassified kind) map to config conflict; same as prior discard arm.
            MailerConfigurationLoadFailureKind.None =>
                (SetupInspectReason.ConfigConflict, SetupInspectCredentialStatus.NotApplicable),
        };

        var mount = managed
            ? Attestation(SetupInspectIntegrityResult.NotVerified, reason)
            : Attestation(SetupInspectIntegrityResult.NotManaged, SetupInspectReason.MetadataMissing);

        return BuildResult(
            mailerVersion,
            managed,
            recorded,
            new SetupInspectEffectiveSummary { CredentialStatus = credentialStatus },
            mount,
            DeriveProvisionalIntegrity(managed, mount),
            failureKind == MailerConfigurationLoadFailureKind.TenantsMissing
                ? SetupInspectSourceIds.NotApplicable
                : SetupInspectSourceIds.ContainerTenants,
            SetupInspectSourceIds.NotApplicable,
            reason);
    }

    private static SetupInspectAttestationSummary ResolveMountAttestation(
        IConfiguration configuration,
        bool managed,
        SetupRecordedMetadata? recorded,
        IReadOnlyCollection<string> requiredMemberIds,
        DateTimeOffset utcNow)
    {
        if (!managed || recorded is null)
        {
            return Attestation(SetupInspectIntegrityResult.NotManaged, SetupInspectReason.MetadataMissing);
        }

        var verifierPath = configuration[MountVerifierPathEnv];
        if (string.IsNullOrWhiteSpace(verifierPath) || !File.Exists(verifierPath))
        {
            return Attestation(SetupInspectIntegrityResult.NotVerified, SetupInspectReason.VerifierMissing);
        }

        SetupMountVerifierDocument? verifier;
        try
        {
            var json = File.ReadAllText(verifierPath);
            verifier = JsonSerializer.Deserialize(json, SetupInspectJsonContext.Default.SetupMountVerifierDocument);
        }
        catch
        {
            return Attestation(SetupInspectIntegrityResult.NotVerified, SetupInspectReason.VerifierMalformed);
        }

        if (verifier is null
            || string.IsNullOrWhiteSpace(verifier.BundleId)
            || string.IsNullOrWhiteSpace(verifier.SessionKey)
            || string.IsNullOrWhiteSpace(verifier.SessionNonce)
            || verifier.Members is null)
        {
            return Attestation(SetupInspectIntegrityResult.NotVerified, SetupInspectReason.VerifierMalformed);
        }

        return SetupMountAttestation.Verify(
            verifier,
            recorded.BundleId,
            requiredMemberIds,
            memberId => ResolveMountedMemberBytes(configuration, memberId),
            utcNow);
    }

    private static byte[]? ResolveMountedMemberBytes(IConfiguration configuration, string memberId)
    {
        if (string.Equals(memberId, SetupMountAttestation.AcsConnectionStringMemberId, StringComparison.Ordinal))
        {
            var filePath = configuration["ACS_CONNECTION_STRING_FILE"];
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                return File.ReadAllBytes(filePath);
            }

            return null;
        }

        const string envPrefix = "env:";
        if (!memberId.StartsWith(envPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var envKey = memberId[envPrefix.Length..];
        if (string.IsNullOrWhiteSpace(envKey))
        {
            return null;
        }

        var value = configuration[envKey] ?? Environment.GetEnvironmentVariable(envKey);
        return value is null ? null : Encoding.UTF8.GetBytes(value);
    }

    private static SetupInspectAttestationSummary DeriveProvisionalIntegrity(
        bool managed,
        SetupInspectAttestationSummary mount)
    {
        if (!managed
            || string.Equals(mount.Result, SetupInspectIntegrityResult.NotManaged, StringComparison.Ordinal))
        {
            return Attestation(
                SetupInspectIntegrityResult.NotManaged,
                SetupInspectReason.MetadataMissing,
                scope: "provisional");
        }

        if (string.Equals(mount.Result, SetupInspectIntegrityResult.InvalidMetadata, StringComparison.Ordinal))
        {
            return Attestation(SetupInspectIntegrityResult.InvalidMetadata, mount.Reason, scope: "provisional");
        }

        if (string.Equals(mount.Result, SetupInspectIntegrityResult.Mismatch, StringComparison.Ordinal))
        {
            return Attestation(
                SetupInspectIntegrityResult.Mismatch,
                mount.Reason ?? SetupInspectReason.MountMismatch,
                scope: "provisional");
        }

        return Attestation(
            SetupInspectIntegrityResult.NotVerified,
            mount.Reason ?? SetupInspectReason.HostAtRestPending,
            scope: "provisional");
    }

    private static string? ComputeEffectiveFingerprint(
        IConfiguration configuration,
        MailerTenantsFile tenants,
        string tenantsPath,
        SetupRecordedMetadata? recorded)
    {
        if (recorded is null || !SetupModeParser.TryParse(recorded.Mode, out var mode))
        {
            return SetupCanonicalPayload.FingerprintSha256(
                SetupCanonicalPayload.BuildFromWireMode(
                    "not-managed",
                    tenants,
                    CollectPublicCompose(configuration, recorded?.BundleId),
                    TryLoadPlatformSender(tenantsPath),
                    ConfigurationBooleanReader.Read(configuration, "AMANE_ADMIN_ENABLED", defaultValue: false)));
        }

        var canonical = SetupCanonicalPayload.BuildForRecordedSchema(
            mode,
            tenants,
            CollectPublicCompose(configuration, recorded.BundleId),
            TryLoadPlatformSender(tenantsPath),
            ConfigurationBooleanReader.Read(configuration, "AMANE_ADMIN_ENABLED", defaultValue: false),
            recorded.SchemaVersion);
        return SetupCanonicalPayload.FingerprintSha256(canonical);
    }

    private static SortedDictionary<string, string> CollectPublicCompose(
        IConfiguration configuration,
        string? bundleId)
    {
        var compose = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in ManagedEnvKeyCatalog.PublicNonSecretKeys)
        {
            var value = configuration[key];
            if (value is not null)
            {
                compose[key] = value;
            }
        }

        return SetupFingerprintComposeNormalizer.Normalize(compose, bundleId);
    }

    private static PlatformSenderFile? TryLoadPlatformSender(string tenantsPath)
    {
        var tenantsDir = Path.GetDirectoryName(tenantsPath);
        if (string.IsNullOrWhiteSpace(tenantsDir))
        {
            return null;
        }

        var senderPath = Path.Combine(tenantsDir, PlatformSenderFile.CanonicalFileName);
        if (!File.Exists(senderPath))
        {
            return null;
        }

        return JsonSerializer.Deserialize(
            File.ReadAllText(senderPath),
            SetupJsonContext.Default.PlatformSenderFile);
    }

    private static (string Status, string Source) ResolveCredentialStatus(
        MailerOptions options,
        IReadOnlyList<MailerTenant> tenants,
        IConfiguration configuration)
    {
        var needsAcs = tenants.Any(t =>
            string.Equals(options.ResolveProvider(t), "acs", StringComparison.Ordinal));
        if (!needsAcs)
        {
            return (SetupInspectCredentialStatus.NotApplicable, SetupInspectSourceIds.NotApplicable);
        }

        var resolution = MailerAcsCredential.Resolve(configuration);
        switch (resolution.Source)
        {
            case MailerAcsCredentialSource.File:
                if (ConfigurationPlaceholderDetector.LooksLikePlaceholder(resolution.Value))
                {
                    return (SetupInspectCredentialStatus.Invalid, SetupInspectSourceIds.ContainerAcsFile);
                }

                return (SetupInspectCredentialStatus.Loaded, SetupInspectSourceIds.ContainerAcsFile);

            case MailerAcsCredentialSource.Environment:
                if (ConfigurationPlaceholderDetector.LooksLikePlaceholder(resolution.Value))
                {
                    return (SetupInspectCredentialStatus.Invalid, SetupInspectSourceIds.ContainerAcsEnv);
                }

                return (SetupInspectCredentialStatus.Loaded, SetupInspectSourceIds.ContainerAcsEnv);

            default:
                {
                    var fileConfigured = !string.IsNullOrWhiteSpace(configuration["ACS_CONNECTION_STRING_FILE"]);
                    var source = fileConfigured || resolution.RequiredFile
                        ? SetupInspectSourceIds.ContainerAcsFile
                        : SetupInspectSourceIds.ContainerAcsEnv;
                    return (SetupInspectCredentialStatus.Missing, source);
                }
        }
    }

    private static string? SummarizeProvider(MailerOptions options, IReadOnlyList<MailerTenant> tenants)
    {
        static bool IsKnown(string provider) =>
            provider.Equals("mailpit", StringComparison.Ordinal)
            || provider.Equals("acs", StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(options.ProviderOverride))
        {
            var overrideProvider = options.ProviderOverride.Trim().ToLowerInvariant();
            return IsKnown(overrideProvider) ? overrideProvider : null;
        }

        var providers = tenants
            .Select(t => options.ResolveProvider(t).Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        if (providers.Length == 0 || providers.Any(p => !IsKnown(p)))
        {
            return null;
        }

        return providers.Length == 1 ? providers[0] : string.Join("+", providers);
    }

    private enum RecordedLoadKind
    {
        Absent,
        Present,
        Malformed,
        UnsupportedSchema,
    }

    private sealed class RecordedLoadResult
    {
        public required RecordedLoadKind Kind { get; init; }
        public SetupRecordedMetadata? Metadata { get; init; }
    }

    private static RecordedLoadResult TryLoadRecorded(IConfiguration configuration)
    {
        var path = configuration[RecordedMetadataPathEnv]
            ?? SetupBundleLayout.ContainerRecordedMetadataPath;

        if (!File.Exists(path))
        {
            return new RecordedLoadResult { Kind = RecordedLoadKind.Absent };
        }

        try
        {
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new RecordedLoadResult { Kind = RecordedLoadKind.Malformed };
            }

            var metadata = JsonSerializer.Deserialize(json, SetupJsonContext.Default.SetupRecordedMetadata);
            if (metadata is null
                || string.IsNullOrWhiteSpace(metadata.BundleId)
                || string.IsNullOrWhiteSpace(metadata.ConfigurationFingerprint)
                || string.IsNullOrWhiteSpace(metadata.Mode)
                || string.IsNullOrWhiteSpace(metadata.CreatedAt))
            {
                return new RecordedLoadResult { Kind = RecordedLoadKind.Malformed };
            }

            if (metadata.SchemaVersion < SetupBundleLayout.MinimumSupportedRecordedSchemaVersion)
            {
                return new RecordedLoadResult { Kind = RecordedLoadKind.Malformed };
            }

            if (metadata.SchemaVersion > SetupBundleLayout.RecordedSchemaVersion)
            {
                return new RecordedLoadResult { Kind = RecordedLoadKind.UnsupportedSchema };
            }

            if ((metadata.SchemaVersion == 1 && metadata.AdminBootstrapExpectation is not null)
                || (metadata.AdminBootstrapRequested
                    && (metadata.AdminBootstrapExpectation is not { } expectation
                        || !AdminBootstrapOperationId.TryParse(expectation.OperationId, out _))))
            {
                return new RecordedLoadResult { Kind = RecordedLoadKind.Malformed };
            }

            if (!SetupModeParser.TryParse(metadata.Mode, out _))
            {
                return new RecordedLoadResult { Kind = RecordedLoadKind.Malformed };
            }

            if (!FingerprintRegex().IsMatch(metadata.ConfigurationFingerprint))
            {
                return new RecordedLoadResult { Kind = RecordedLoadKind.Malformed };
            }

            if (!DateTimeOffset.TryParse(
                    metadata.CreatedAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out _))
            {
                return new RecordedLoadResult { Kind = RecordedLoadKind.Malformed };
            }

            if (!BundleIdRegex().IsMatch(metadata.BundleId))
            {
                return new RecordedLoadResult { Kind = RecordedLoadKind.Malformed };
            }

            return new RecordedLoadResult { Kind = RecordedLoadKind.Present, Metadata = metadata };
        }
        catch
        {
            return new RecordedLoadResult { Kind = RecordedLoadKind.Malformed };
        }
    }

    private static SetupInspectEffectiveResult BuildResult(
        string mailerVersion,
        bool managed,
        SetupInspectRecordedSummary? recorded,
        SetupInspectEffectiveSummary effective,
        SetupInspectAttestationSummary mount,
        SetupInspectAttestationSummary integrity,
        string tenantSource,
        string credentialSource,
        string? reason) =>
        new()
        {
            SchemaVersion = SetupInspectEffectiveResult.CurrentSchemaVersion,
            MailerVersion = mailerVersion,
            Managed = managed,
            Recorded = recorded,
            Effective = effective,
            MountAttestation = mount,
            BundleIntegrity = integrity,
            TenantConfigurationSource = tenantSource,
            CredentialSource = credentialSource,
            Reason = reason,
        };

    private static SetupInspectEffectiveSummary EmptyEffective() =>
        new() { CredentialStatus = SetupInspectCredentialStatus.NotApplicable };

    private static SetupInspectAttestationSummary Attestation(
        string result,
        string? reason,
        string? scope = "container-mount") =>
        new()
        {
            Result = result,
            Reason = reason,
            Scope = scope,
        };

    internal static string ResolveMailerVersion()
    {
        var assembly = typeof(SetupInspectEffectiveEngine).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
