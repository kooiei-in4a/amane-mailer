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
                    && residual.Source.ActivationGeneration == active.ActivationGeneration)
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

        var bundleRoot = SetupBundleLayout.BundleRoot(layout.ManagedRoot, active.BundleId);
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
        if (recorded is null
            || !string.Equals(recorded.BundleId, active.BundleId, StringComparison.Ordinal)
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
