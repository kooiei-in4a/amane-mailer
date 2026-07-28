using System.Reflection;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Configuration;
using Amane.Mailer.Json;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Setup;

/// <summary>
/// Resolves effective non-secret configuration and container mount attestation using the same
/// configuration loader paths as runtime (ADR 0021 D-05). Does not introspect a running process.
/// </summary>
public static class SetupInspectEffectiveEngine
{
    public const string MountVerifierPathEnv = "MAILER_SETUP_MOUNT_VERIFIER_PATH";
    public const string RecordedMetadataPathEnv = "MAILER_SETUP_RECORDED_METADATA_PATH";

    public static SetupInspectEffectiveResult Inspect(
        IConfiguration configuration,
        TimeProvider? timeProvider = null)
    {
        timeProvider ??= TimeProvider.System;
        var utcNow = timeProvider.GetUtcNow();
        var mailerVersion = ResolveMailerVersion();

        var recordedLoad = TryLoadRecorded(configuration);
        if (recordedLoad.Kind == RecordedLoadKind.Malformed)
        {
            return BuildTerminal(
                mailerVersion,
                managed: false,
                recorded: null,
                effective: EmptyEffective(),
                mount: Attestation(
                    SetupInspectIntegrityResult.InvalidMetadata,
                    SetupInspectReason.MetadataMalformed),
                integrity: Attestation(
                    SetupInspectIntegrityResult.InvalidMetadata,
                    SetupInspectReason.MetadataMalformed,
                    scope: "provisional"),
                tenantSource: SetupInspectSourceIds.NotApplicable,
                credentialSource: SetupInspectSourceIds.NotApplicable,
                reason: SetupInspectReason.MetadataMalformed);
        }

        if (recordedLoad.Kind == RecordedLoadKind.UnsupportedSchema)
        {
            return BuildTerminal(
                mailerVersion,
                managed: false,
                recorded: null,
                effective: EmptyEffective(),
                mount: Attestation(
                    SetupInspectIntegrityResult.InvalidMetadata,
                    SetupInspectReason.UnsupportedSchemaVersion),
                integrity: Attestation(
                    SetupInspectIntegrityResult.InvalidMetadata,
                    SetupInspectReason.UnsupportedSchemaVersion,
                    scope: "provisional"),
                tenantSource: SetupInspectSourceIds.NotApplicable,
                credentialSource: SetupInspectSourceIds.NotApplicable,
                reason: SetupInspectReason.UnsupportedSchemaVersion);
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

        if (!TryLoadTenantsFile(configuration, out var tenantsFile, out var tenantsPath, out var tenantsError))
        {
            var tenantsLoadReason = tenantsError ?? SetupInspectReason.ConfigConflict;
            var mountWhenNoTenants = managed
                ? Attestation(SetupInspectIntegrityResult.NotVerified, SetupInspectReason.ConfigConflict)
                : Attestation(SetupInspectIntegrityResult.NotManaged, SetupInspectReason.MetadataMissing);
            return BuildTerminal(
                mailerVersion,
                managed,
                recordedSummary,
                EmptyEffective(),
                mountWhenNoTenants,
                DeriveProvisionalIntegrity(managed, mountWhenNoTenants),
                SetupInspectSourceIds.NotApplicable,
                SetupInspectSourceIds.NotApplicable,
                tenantsLoadReason);
        }

        MailerOptions mailerOptions;
        try
        {
            mailerOptions = MailerOptions.Load(configuration);
        }
        catch
        {
            return BuildTerminal(
                mailerVersion,
                managed,
                recordedSummary,
                EmptyEffective(),
                managed
                    ? Attestation(SetupInspectIntegrityResult.NotVerified, SetupInspectReason.ConfigConflict)
                    : Attestation(SetupInspectIntegrityResult.NotManaged, SetupInspectReason.MetadataMissing),
                managed
                    ? Attestation(
                        SetupInspectIntegrityResult.NotVerified,
                        SetupInspectReason.ConfigConflict,
                        scope: "provisional")
                    : Attestation(
                        SetupInspectIntegrityResult.NotManaged,
                        SetupInspectReason.MetadataMissing,
                        scope: "provisional"),
                SetupInspectSourceIds.ContainerTenants,
                SetupInspectSourceIds.NotApplicable,
                SetupInspectReason.ConfigConflict);
        }

        var providerSummary = SummarizeProvider(mailerOptions, tenantsFile);
        var liveSending = tenantsFile.Tenants.Any(t => t.LiveSending);
        var (credentialStatus, credentialSource) = ResolveCredentialStatus(mailerOptions, tenantsFile, configuration);

        string? effectiveFingerprint = null;
        bool? fingerprintsMatch = null;
        try
        {
            effectiveFingerprint = ComputeEffectiveFingerprint(
                configuration,
                tenantsFile,
                tenantsPath,
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
            // Fingerprint failure is non-secret; keep inspection usable.
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

        var mount = ResolveMountAttestation(
            configuration,
            managed,
            recordedLoad.Metadata,
            utcNow);

        var integrity = DeriveProvisionalIntegrity(managed, mount);
        var reason = integrity.Reason
            ?? mount.Reason
            ?? (managed ? null : SetupInspectReason.MetadataMissing);

        return new SetupInspectEffectiveResult
        {
            SchemaVersion = SetupInspectEffectiveResult.CurrentSchemaVersion,
            MailerVersion = mailerVersion,
            Managed = managed,
            Recorded = recordedSummary,
            Effective = effective,
            MountAttestation = mount,
            BundleIntegrity = integrity,
            TenantConfigurationSource = SetupInspectSourceIds.ContainerTenants,
            CredentialSource = credentialSource,
            Reason = reason,
        };
    }

    private static SetupInspectAttestationSummary ResolveMountAttestation(
        IConfiguration configuration,
        bool managed,
        SetupRecordedMetadata? recorded,
        DateTimeOffset utcNow)
    {
        if (!managed || recorded is null)
        {
            return Attestation(SetupInspectIntegrityResult.NotManaged, SetupInspectReason.MetadataMissing);
        }

        var verifierPath = configuration[MountVerifierPathEnv]
            ?? Environment.GetEnvironmentVariable(MountVerifierPathEnv);
        if (string.IsNullOrWhiteSpace(verifierPath))
        {
            return Attestation(SetupInspectIntegrityResult.NotVerified, SetupInspectReason.VerifierMissing);
        }

        if (!File.Exists(verifierPath))
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
            || verifier.Members is null)
        {
            return Attestation(SetupInspectIntegrityResult.NotVerified, SetupInspectReason.VerifierMalformed);
        }

        return SetupMountAttestation.Verify(
            verifier,
            recorded.BundleId,
            memberId => ResolveMountedMemberBytes(configuration, memberId),
            utcNow);
    }

    private static byte[]? ResolveMountedMemberBytes(IConfiguration configuration, string memberId)
    {
        if (string.Equals(memberId, SetupMountAttestation.AcsConnectionStringMemberId, StringComparison.Ordinal))
        {
            var filePath = configuration["ACS_CONNECTION_STRING_FILE"]
                ?? Environment.GetEnvironmentVariable("ACS_CONNECTION_STRING_FILE");
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                return File.ReadAllBytes(filePath);
            }

            return null;
        }

        const string envPrefix = "env:";
        if (memberId.StartsWith(envPrefix, StringComparison.Ordinal))
        {
            var envKey = memberId[envPrefix.Length..];
            if (string.IsNullOrWhiteSpace(envKey))
            {
                return null;
            }

            // Only allowlisted secret-valued keys (or known tenant token names) are readable.
            if (!ManagedEnvKeyCatalog.SecretValuedEnvironmentKeys.Contains(envKey)
                && !envKey.StartsWith("MAIL_SERVICE_TOKEN", StringComparison.Ordinal)
                && !envKey.EndsWith("_WEBHOOK_SECRET", StringComparison.Ordinal))
            {
                return null;
            }

            var value = configuration[envKey] ?? Environment.GetEnvironmentVariable(envKey);
            return value is null ? null : Encoding.UTF8.GetBytes(value);
        }

        return null;
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
            return Attestation(
                SetupInspectIntegrityResult.InvalidMetadata,
                mount.Reason,
                scope: "provisional");
        }

        if (string.Equals(mount.Result, SetupInspectIntegrityResult.Mismatch, StringComparison.Ordinal))
        {
            // Fail-closed: mount mismatch cannot become matched after host integration.
            return Attestation(
                SetupInspectIntegrityResult.Mismatch,
                mount.Reason ?? SetupInspectReason.MountMismatch,
                scope: "provisional");
        }

        // Mount matched or not-verified: one-shot must not claim final host+mount matched.
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
        var modeWire = recorded?.Mode;
        if (string.IsNullOrWhiteSpace(modeWire))
        {
            modeWire = "not-managed";
        }

        var compose = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in ManagedEnvKeyCatalog.PublicNonSecretKeys)
        {
            // IConfiguration already includes process env for the CLI host. Do not also
            // merge Environment.GetEnvironmentVariable here or test/host ambient env pollutes fingerprints.
            var value = configuration[key];
            if (value is not null)
            {
                compose[key] = value;
            }
        }

        if (recorded?.BundleId is { Length: > 0 } bundleId)
        {
            foreach (var key in compose.Keys.ToList())
            {
                compose[key] = compose[key]
                    .Replace($"bundles/{bundleId}/", "bundles/<bundle-id>/", StringComparison.Ordinal);
            }
        }

        PlatformSenderFile? platformSender = null;
        var tenantsDir = Path.GetDirectoryName(tenantsPath);
        if (!string.IsNullOrWhiteSpace(tenantsDir))
        {
            var senderPath = Path.Combine(tenantsDir, PlatformSenderFile.CanonicalFileName);
            if (File.Exists(senderPath))
            {
                platformSender = JsonSerializer.Deserialize(
                    File.ReadAllText(senderPath),
                    SetupJsonContext.Default.PlatformSenderFile);
            }
        }

        var adminRequested = ConfigurationBooleanReader.Read(
            configuration,
            "AMANE_ADMIN_ENABLED",
            defaultValue: false);

        var canonical = SetupCanonicalPayload.BuildFromWireMode(
            modeWire,
            tenants,
            compose,
            platformSender,
            adminRequested);
        return SetupCanonicalPayload.FingerprintSha256(canonical);
    }

    private static (string Status, string Source) ResolveCredentialStatus(
        MailerOptions options,
        MailerTenantsFile tenants,
        IConfiguration configuration)
    {
        var needsAcs = tenants.Tenants.Any(t =>
            string.Equals(options.ResolveProvider(t), "acs", StringComparison.Ordinal));

        if (!needsAcs)
        {
            return (SetupInspectCredentialStatus.NotApplicable, SetupInspectSourceIds.NotApplicable);
        }

        var filePath = configuration["ACS_CONNECTION_STRING_FILE"]
            ?? Environment.GetEnvironmentVariable("ACS_CONNECTION_STRING_FILE");
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (!File.Exists(filePath))
            {
                return (SetupInspectCredentialStatus.Missing, SetupInspectSourceIds.ContainerAcsFile);
            }

            var text = File.ReadAllText(filePath).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return (SetupInspectCredentialStatus.Missing, SetupInspectSourceIds.ContainerAcsFile);
            }

            if (ConfigurationPlaceholderDetector.LooksLikePlaceholder(text))
            {
                return (SetupInspectCredentialStatus.Invalid, SetupInspectSourceIds.ContainerAcsFile);
            }

            return (SetupInspectCredentialStatus.Loaded, SetupInspectSourceIds.ContainerAcsFile);
        }

        if (!string.IsNullOrWhiteSpace(options.AcsConnectionString))
        {
            if (ConfigurationPlaceholderDetector.LooksLikePlaceholder(options.AcsConnectionString))
            {
                return (SetupInspectCredentialStatus.Invalid, SetupInspectSourceIds.ContainerAcsEnv);
            }

            return (SetupInspectCredentialStatus.Loaded, SetupInspectSourceIds.ContainerAcsEnv);
        }

        return (SetupInspectCredentialStatus.Missing, SetupInspectSourceIds.ContainerAcsFile);
    }

    private static string SummarizeProvider(MailerOptions options, MailerTenantsFile tenants)
    {
        if (!string.IsNullOrWhiteSpace(options.ProviderOverride))
        {
            return options.ProviderOverride.Trim().ToLowerInvariant();
        }

        var providers = tenants.Tenants
            .Select(t => options.ResolveProvider(t).Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        return providers.Length switch
        {
            0 => "none",
            1 => providers[0],
            _ => string.Join("+", providers),
        };
    }

    private static bool TryLoadTenantsFile(
        IConfiguration configuration,
        out MailerTenantsFile tenants,
        out string tenantsPath,
        out string? errorReason)
    {
        tenants = null!;
        errorReason = null;
        tenantsPath = configuration["Mailer:TenantsPath"]
            ?? configuration["MAILER_TENANTS_PATH"]
            ?? Path.Combine(AppContext.BaseDirectory, "config", "mailer", "tenants.example.json");

        if (!File.Exists(tenantsPath))
        {
            errorReason = SetupInspectReason.TenantsMissing;
            return false;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize(
                File.ReadAllText(tenantsPath),
                MailerJsonContext.Default.MailerTenantsFile);
            if (loaded is null)
            {
                errorReason = SetupInspectReason.ConfigConflict;
                return false;
            }

            loaded.Validate();
            foreach (var tenant in loaded.Tenants)
            {
                tenant.Validate();
            }

            tenants = loaded;
            return true;
        }
        catch
        {
            errorReason = SetupInspectReason.ConfigConflict;
            return false;
        }
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
            ?? Environment.GetEnvironmentVariable(RecordedMetadataPathEnv)
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

            if (document.RootElement.TryGetProperty("schemaVersion", out var schemaEl)
                && schemaEl.TryGetInt32(out var schemaVersion)
                && schemaVersion > SetupBundleLayout.RecordedSchemaVersion)
            {
                return new RecordedLoadResult { Kind = RecordedLoadKind.UnsupportedSchema };
            }

            var metadata = JsonSerializer.Deserialize(json, SetupInspectJsonContext.Default.SetupRecordedMetadata);
            if (metadata is null
                || string.IsNullOrWhiteSpace(metadata.BundleId)
                || string.IsNullOrWhiteSpace(metadata.ConfigurationFingerprint)
                || string.IsNullOrWhiteSpace(metadata.Mode)
                || string.IsNullOrWhiteSpace(metadata.CreatedAt))
            {
                return new RecordedLoadResult { Kind = RecordedLoadKind.Malformed };
            }

            if (metadata.SchemaVersion < 1)
            {
                return new RecordedLoadResult { Kind = RecordedLoadKind.Malformed };
            }

            if (metadata.SchemaVersion > SetupBundleLayout.RecordedSchemaVersion)
            {
                return new RecordedLoadResult { Kind = RecordedLoadKind.UnsupportedSchema };
            }

            return new RecordedLoadResult { Kind = RecordedLoadKind.Present, Metadata = metadata };
        }
        catch
        {
            return new RecordedLoadResult { Kind = RecordedLoadKind.Malformed };
        }
    }

    private static SetupInspectEffectiveResult BuildTerminal(
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
        new()
        {
            CredentialStatus = SetupInspectCredentialStatus.NotApplicable,
        };

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
