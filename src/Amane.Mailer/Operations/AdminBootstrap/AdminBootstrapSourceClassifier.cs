using System.Text.Json;
using Amane.Mailer.Admin;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Operations.AdminBootstrap;

/// <summary>
/// Evidence-based source classification. Unknown is terminal before candidate generation; this
/// type never adopts an enabled Manual/v1 Admin deployment.
/// </summary>
internal sealed class AdminBootstrapSourceClassifier(
    ISetupFileSystem fileSystem,
    AdminBootstrapOwnershipStore ownershipStore)
{
    internal bool TryReadActiveAuthority(
        TrustedSetupHostLayout layout,
        out SetupActivePointer? active,
        out SetupRecordedMetadata? recorded) =>
        TryReadSource(layout, out active, out recorded, out _, out _)
        && active is not null
        && recorded is not null;

    internal SourceAdminDisposition Classify(
        TrustedSetupHostLayout layout,
        AdminBootstrapDatabaseSnapshot database)
    {
        try
        {
            if (!TryReadSource(
                    layout,
                    out var active,
                    out var recorded,
                    out var compose,
                    out var secrets)
                || active is null
                || recorded is null)
            {
                return SourceAdminDisposition.Unknown;
            }

            var adminEnabled = compose.TryGetValue("AMANE_ADMIN_ENABLED", out var enabled)
                && string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase);
            var current = ownershipStore.ReadCurrent(layout.ManagedRoot);
            if (!adminEnabled)
            {
                if (database.Classification == AdminBootstrapDatabaseClassification.Fresh)
                    return SourceAdminDisposition.DisabledMain;

                if (database.Classification == AdminBootstrapDatabaseClassification.ManagedSameUser
                    && current.Kind == AdminBootstrapOwnershipReadKind.Valid
                    && current.Document is { } residual
                    && string.Equals(
                        residual.State,
                        AdminBootstrapOwnershipState.ResidualAfterConfigRollback,
                        StringComparison.Ordinal)
                    && string.Equals(residual.Source.BundleId, active.BundleId, StringComparison.Ordinal)
                    && residual.Source.ActivationGeneration == active.ActivationGeneration
                    && TryValidateResidualAuthority(layout, residual, database))
                {
                    return SourceAdminDisposition.DisabledMain;
                }

                return SourceAdminDisposition.Unknown;
            }

            if (recorded.SchemaVersion < 2
                || recorded.AdminBootstrapExpectation is not { } expectation
                || current.Kind != AdminBootstrapOwnershipReadKind.Valid
                || current.Document is not { } succeeded
                || !string.Equals(
                    succeeded.State,
                    AdminBootstrapOwnershipState.Succeeded,
                    StringComparison.Ordinal)
                || !string.Equals(succeeded.OperationId, expectation.OperationId, StringComparison.Ordinal)
                || !string.Equals(succeeded.Candidate.BundleId, active.BundleId, StringComparison.Ordinal)
                || succeeded.Candidate.ExpectedActivationGeneration != active.ActivationGeneration
                || database.Classification != AdminBootstrapDatabaseClassification.ManagedSameUser
                || !compose.TryGetValue("AMANE_ADMIN_USERNAME", out var username)
                || !string.Equals(username, database.Username, StringComparison.Ordinal)
                || !secrets.TryGetValue("AMANE_ADMIN_PASSWORD_HASH", out var effectiveHash)
                || !string.Equals(effectiveHash, database.AppliedPasswordHash, StringComparison.Ordinal)
                || !string.Equals(effectiveHash, database.UserPasswordHash, StringComparison.Ordinal)
                || database.AdminConfigCredentialEpoch
                    != expectation.After.AdminConfigCredentialEpoch
                || database.AdminUserCredentialEpoch
                    != expectation.After.AdminUserCredentialEpoch
                || !string.Equals(
                    database.ScopeFingerprint,
                    expectation.After.ScopeFingerprint,
                    StringComparison.Ordinal))
            {
                return SourceAdminDisposition.Unknown;
            }

            return SourceAdminDisposition.EnabledManagedSameUser;
        }
        catch (IOException)
        {
            return SourceAdminDisposition.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return SourceAdminDisposition.Unknown;
        }
        catch (JsonException)
        {
            return SourceAdminDisposition.Unknown;
        }
    }

    private bool TryValidateResidualAuthority(
        TrustedSetupHostLayout layout,
        AdminBootstrapOwnershipDocument residual,
        AdminBootstrapDatabaseSnapshot database) =>
        TryReadVerifiedCandidateAdminAuthority(
            layout,
            residual,
            out var candidateExpectation,
            out var username,
            out var candidateHash)
        && candidateExpectation is not null
        && MatchesResidualDatabaseAuthority(
            residual,
            candidateExpectation,
            database,
            username!,
            candidateHash!);

    /// <summary>
    /// Reads the candidate Admin username and effective password hash only from a finalized
    /// candidate bundle whose host at-rest integrity, recorded schema, configuration fingerprint,
    /// image compatibility, and recorded operation id still match the ownership authority.
    /// </summary>
    internal bool TryReadVerifiedCandidateAdminAuthority(
        TrustedSetupHostLayout layout,
        AdminBootstrapOwnershipDocument document,
        out SetupAdminBootstrapExpectation? candidateExpectation,
        out string? username,
        out string? passwordHash)
    {
        candidateExpectation = null;
        username = null;
        passwordHash = null;

        try
        {
            var validation = SetupBundleStaticValidator.TryValidateFinalizedBundle(
                fileSystem,
                layout,
                document.Candidate.BundleId,
                out var recorded,
                out var hostAtRest);
            if (!validation.IsSuccess
                || recorded is null
                || !string.Equals(hostAtRest, SetupIntegrityMerger.Matched, StringComparison.Ordinal)
                || !string.Equals(
                    recorded.BundleId,
                    document.Candidate.BundleId,
                    StringComparison.Ordinal)
                || recorded.SchemaVersion < 2
                || !string.Equals(
                    recorded.ConfigurationFingerprint,
                    document.Candidate.ConfigurationFingerprint,
                    StringComparison.Ordinal)
                || SetupBundleStaticValidator.ClassifyImageCompatibility(layout, recorded) is not null
                || recorded.AdminBootstrapExpectation is not { } expectation
                || !string.Equals(
                    expectation.OperationId,
                    document.OperationId,
                    StringComparison.Ordinal)
                || !TryReadBundleEnv(layout, document.Candidate.BundleId, out var compose, out var secrets)
                || !compose.TryGetValue("AMANE_ADMIN_USERNAME", out var candidateUsername)
                || !secrets.TryGetValue("AMANE_ADMIN_PASSWORD_HASH", out var candidateHash)
                || string.IsNullOrWhiteSpace(candidateUsername)
                || !AdminPasswordHasher.IsSupportedHash(candidateHash))
            {
                return false;
            }

            candidateExpectation = expectation;
            username = candidateUsername;
            passwordHash = candidateHash;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool MatchesResidualDatabaseAuthority(
        AdminBootstrapOwnershipDocument residual,
        SetupAdminBootstrapExpectation candidateExpectation,
        AdminBootstrapDatabaseSnapshot database,
        string candidateUsername,
        string candidateHash) =>
        string.Equals(
            residual.OperationId,
            candidateExpectation.OperationId,
            StringComparison.Ordinal)
        && string.Equals(
            residual.OperationId,
            residual.ExpectedDatabase.OperationId,
            StringComparison.Ordinal)
        && Matches(database, residual.ExpectedDatabase.After)
        && Matches(database, candidateExpectation.After)
        && string.Equals(candidateUsername, database.Username, StringComparison.Ordinal)
        && string.Equals(candidateHash, database.AppliedPasswordHash, StringComparison.Ordinal)
        && string.Equals(candidateHash, database.UserPasswordHash, StringComparison.Ordinal);

    private static bool Matches(
        AdminBootstrapDatabaseSnapshot database,
        SetupAdminDatabaseExpectationState expected) =>
        string.Equals(database.Classification, expected.Classification, StringComparison.Ordinal)
        && database.AdminConfigCount == expected.AdminConfigCount
        && database.AdminUserCount == expected.AdminUserCount
        && database.AdminConfigCredentialEpoch == expected.AdminConfigCredentialEpoch
        && database.AdminUserCredentialEpoch == expected.AdminUserCredentialEpoch
        && string.Equals(database.ScopeFingerprint, expected.ScopeFingerprint, StringComparison.Ordinal);

    private bool TryReadSource(
        TrustedSetupHostLayout layout,
        out SetupActivePointer? active,
        out SetupRecordedMetadata? recorded,
        out IReadOnlyDictionary<string, string> compose,
        out IReadOnlyDictionary<string, string> secrets)
    {
        active = null;
        recorded = null;
        compose = new Dictionary<string, string>(StringComparer.Ordinal);
        secrets = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!fileSystem.FileExists(layout.ActivePointerPath)
            || !SetupActivePointer.TryParse(
                System.Text.Encoding.UTF8.GetString(fileSystem.ReadAllBytes(layout.ActivePointerPath)),
                out active)
            || active is null)
        {
            return false;
        }

        if (!TryReadBundle(layout, active.BundleId, out recorded, out compose, out secrets))
            return false;

        return recorded is not null
            && string.Equals(recorded.BundleId, active.BundleId, StringComparison.Ordinal);
    }

    private bool TryReadBundle(
        TrustedSetupHostLayout layout,
        string bundleId,
        out SetupRecordedMetadata? recorded,
        out IReadOnlyDictionary<string, string> compose,
        out IReadOnlyDictionary<string, string> secrets)
    {
        recorded = null;
        compose = new Dictionary<string, string>(StringComparer.Ordinal);
        secrets = new Dictionary<string, string>(StringComparer.Ordinal);

        var bundleRoot = SetupBundleLayout.BundleRoot(layout.ManagedRoot, bundleId);
        var recordedPath = Path.Combine(
            SetupBundleLayout.MetadataDir(bundleRoot),
            SetupBundleLayout.RecordedMetadataFileName);
        var composePath = Path.Combine(
            SetupBundleLayout.EnvDir(bundleRoot),
            SetupBundleLayout.ComposeEnvFileName);
        var secretsPath = Path.Combine(
            SetupBundleLayout.EnvDir(bundleRoot),
            SetupBundleLayout.SecretsEnvFileName);
        if (!fileSystem.FileExists(recordedPath)
            || !fileSystem.FileExists(composePath)
            || !fileSystem.FileExists(secretsPath))
        {
            return false;
        }

        recorded = JsonSerializer.Deserialize(
            fileSystem.ReadAllBytes(recordedPath),
            SetupJsonContext.Default.SetupRecordedMetadata);
        return recorded is not null
            && string.Equals(recorded.BundleId, bundleId, StringComparison.Ordinal)
            && TryReadBundleEnv(layout, bundleId, out compose, out secrets);
    }

    private bool TryReadBundleEnv(
        TrustedSetupHostLayout layout,
        string bundleId,
        out IReadOnlyDictionary<string, string> compose,
        out IReadOnlyDictionary<string, string> secrets)
    {
        compose = new Dictionary<string, string>(StringComparer.Ordinal);
        secrets = new Dictionary<string, string>(StringComparer.Ordinal);

        var envDir = SetupBundleLayout.EnvDir(SetupBundleLayout.BundleRoot(layout.ManagedRoot, bundleId));
        var composePath = Path.Combine(envDir, SetupBundleLayout.ComposeEnvFileName);
        var secretsPath = Path.Combine(envDir, SetupBundleLayout.SecretsEnvFileName);
        if (!fileSystem.FileExists(composePath)
            || !fileSystem.FileExists(secretsPath)
            || !ManagedComposeEnvComposer.TryParseEnvFile(
                fileSystem.ReadAllBytes(composePath),
                out var parsedCompose,
                out _)
            || !ManagedComposeEnvComposer.TryParseEnvFile(
                fileSystem.ReadAllBytes(secretsPath),
                out var parsedSecrets,
                out _))
        {
            return false;
        }

        compose = parsedCompose;
        secrets = parsedSecrets;
        return true;
    }
}
