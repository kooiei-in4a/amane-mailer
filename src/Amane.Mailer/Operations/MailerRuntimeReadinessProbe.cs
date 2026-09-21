using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Setup;
using Amane.Mailer.Worker;

namespace Amane.Mailer.Operations;

/// <summary>
/// Shared read-only readiness probe for the health endpoint and Admin overview.
/// Provider-secret absence deliberately bypasses readiness observation, matching /readyz.
/// </summary>
internal sealed class MailerRuntimeReadinessProbe(
    InstanceRuntimeState runtimeState,
    SqlMigrationRunner migrationRunner,
    WorkerServiceStatus serviceStatus,
    MailRequestRepository repository,
    MailerHealthcheckOptions healthcheckOptions,
    MailerReadinessEvaluator readinessEvaluator,
    IConfiguration configuration)
{
    public Task<MailerReadinessResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (runtimeState.IsInitialized
            && string.Equals(runtimeState.ProviderType, "acs", StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(runtimeState.ProviderSecretRef)
                || !FirstRunSetupStorage.TryReadValidAcsSecret(runtimeState.ProviderSecretRef, out _)))
        {
            return Task.FromResult(
                MailerReadinessResult.NotReady(MailerReadinessReasons.ProviderSecretMissing));
        }

        var workerEnabled = MailerWorkerOptions.IsEnabled(configuration);
        return readinessEvaluator.EvaluateAsync(
            migrationRunner,
            serviceStatus,
            repository,
            healthcheckOptions,
            workerEnabled,
            cancellationToken);
    }
}
