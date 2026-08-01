using Amane.Mailer.Configuration;
using Amane.Mailer.Setup;
using Amane.Mailer.Setup.Assistant;
using Amane.Mailer.Setup.NonInteractive;
using Amane.Mailer.Tests.Setup;
using Amane.Mailer.Tests.Setup.NonInteractive;

namespace Amane.Mailer.Tests.Setup.Assistant;

public sealed class SetupAssistantMetricsBearerTests
{
    [Fact]
    public void CreateManagedMetricsBearerToken_is_secret_shaped_and_unique()
    {
        var first = SetupAssistantInputs.CreateManagedMetricsBearerToken();
        var second = SetupAssistantInputs.CreateManagedMetricsBearerToken();

        Assert.True(SetupAssistantInputs.IsSecret(first));
        Assert.True(SetupAssistantInputs.IsSecret(second));
        Assert.NotEqual(first, second);
        Assert.Matches("^[0-9a-f]{64}$", first);
        Assert.Matches("^[0-9a-f]{64}$", second);
    }

    [Fact]
    public void Non_interactive_local_mailpit_request_with_managed_metrics_bearer_passes_core_validation()
    {
        var root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "amane-ni-metrics-" + Guid.NewGuid().ToString("N")));
        var parsed = SetupNonInteractiveTestSupport.BuildLocalMailpitInput();
        var collected = SetupNonInteractiveOrchestratorAdapter.BuildCollectedInput(parsed);
        var main = collected.MainSetupInput
            ?? throw new InvalidOperationException("Main setup input missing.");

        var request = new SetupRequest
        {
            Mode = main.Mode,
            ManagedRootPath = root,
            Tenants = main.Tenants,
            TokenSecrets = main.TokenSecrets,
            AcsConnectionString = main.AcsConnectionString,
            PlatformSender = main.PlatformSender,
            MetricsBearerToken = SetupAssistantInputs.CreateManagedMetricsBearerToken(),
            ImageRepository = "ghcr.io/kooiei-in4a/amane-mailer",
            ImageTag = "sha-78486c52ac9eaba50a0a2f758bfeb3f0f31aec82",
        };

        Assert.True(
            SetupRequestValidator.TryValidate(request, out var code, out var message),
            $"code={code} message={message}");

        var dryRun = new SetupRequest
        {
            Mode = request.Mode,
            ManagedRootPath = request.ManagedRootPath,
            DryRun = true,
            Tenants = request.Tenants,
            TokenSecrets = request.TokenSecrets,
            MetricsBearerToken = request.MetricsBearerToken,
            ImageRepository = request.ImageRepository,
            ImageTag = request.ImageTag,
        };
        var result = new SetupCore().GenerateBundle(dryRun);
        Assert.True(result.IsSuccess, result.Code + " " + result.Message);
        Assert.Equal(SetupResultCode.DryRunPlan, result.Code);
    }

    [Fact]
    public void Non_interactive_local_mailpit_request_without_metrics_bearer_is_rejected()
    {
        var root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "amane-ni-metrics-miss-" + Guid.NewGuid().ToString("N")));
        var request = SetupTestFixtures.LocalMailpitRequest(root, dryRun: true);
        request = new SetupRequest
        {
            Mode = request.Mode,
            ManagedRootPath = request.ManagedRootPath,
            DryRun = true,
            Tenants = request.Tenants,
            TokenSecrets = request.TokenSecrets,
            MetricsBearerToken = null,
            ImageRepository = request.ImageRepository,
            ImageTag = request.ImageTag,
        };

        Assert.False(SetupRequestValidator.TryValidate(request, out var code, out _));
        Assert.Equal(SetupResultCode.RejectedValidation, code);
    }
}
