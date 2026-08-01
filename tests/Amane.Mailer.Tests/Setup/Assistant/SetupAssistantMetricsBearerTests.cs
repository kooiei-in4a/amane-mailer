using Amane.Mailer.Setup;
using Amane.Mailer.Setup.Assistant;
using Amane.Mailer.Setup.NonInteractive;
using Amane.Mailer.Tests.Setup;
using Amane.Mailer.Tests.Setup.NonInteractive;

namespace Amane.Mailer.Tests.Setup.Assistant;

public sealed class SetupAssistantMetricsBearerTests
{
    private const string TestImageRepository = "ghcr.io/kooiei-in4a/amane-mailer";
    private const string TestImageTag = "sha-78486c52ac9eaba50a0a2f758bfeb3f0f31aec82";

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
    public void BuildSetupRequest_from_non_interactive_input_sets_managed_metrics_bearer()
    {
        var root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "amane-ni-metrics-wire-" + Guid.NewGuid().ToString("N")));
        var main = BuildLocalMailpitMainInput();
        var ownership = SetupTestFixtures.LinuxRuntimeOwnershipOrNull();

        var request = SetupAssistantOperations.BuildSetupRequest(
            main,
            root,
            TestImageRepository,
            TestImageTag,
            ownership);

        Assert.Equal(main.Mode, request.Mode);
        Assert.Equal(root, request.ManagedRootPath);
        Assert.Same(main.Tenants, request.Tenants);
        Assert.Same(main.TokenSecrets, request.TokenSecrets);
        Assert.Equal(TestImageRepository, request.ImageRepository);
        Assert.Equal(TestImageTag, request.ImageTag);
        Assert.Same(ownership, request.RuntimeFileOwnership);
        Assert.False(string.IsNullOrWhiteSpace(request.MetricsBearerToken));
        Assert.True(SetupAssistantInputs.IsSecret(request.MetricsBearerToken));
        Assert.Matches("^[0-9a-f]{64}$", request.MetricsBearerToken);
    }

    [Fact]
    public void BuildSetupRequest_result_passes_core_validation_and_dry_run()
    {
        var root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "amane-ni-metrics-" + Guid.NewGuid().ToString("N")));
        var request = SetupAssistantOperations.BuildSetupRequest(
            BuildLocalMailpitMainInput(),
            root,
            TestImageRepository,
            TestImageTag,
            SetupTestFixtures.LinuxRuntimeOwnershipOrNull());

        Assert.True(
            SetupRequestValidator.TryValidate(request, out var code, out var message),
            $"code={code} message={message}");

        var dryRun = request with { DryRun = true, RuntimeFileOwnership = null };
        var result = new SetupCore().GenerateBundle(dryRun);
        Assert.True(result.IsSuccess, result.Code + " " + result.Message);
        Assert.Equal(SetupResultCode.DryRunPlan, result.Code);
    }

    [Fact]
    public void Non_interactive_local_mailpit_request_without_metrics_bearer_is_rejected()
    {
        var root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "amane-ni-metrics-miss-" + Guid.NewGuid().ToString("N")));
        var request = SetupTestFixtures.LocalMailpitRequest(root, dryRun: true) with
        {
            MetricsBearerToken = null,
        };

        Assert.False(SetupRequestValidator.TryValidate(request, out var code, out var message));
        Assert.Equal(SetupResultCode.RejectedValidation, code);
        Assert.Contains("Metrics bearer token", message, StringComparison.Ordinal);
    }

    private static SetupAssistantMainSetupInput BuildLocalMailpitMainInput()
    {
        var parsed = SetupNonInteractiveTestSupport.BuildLocalMailpitInput();
        var collected = SetupNonInteractiveOrchestratorAdapter.BuildCollectedInput(parsed);
        return collected.MainSetupInput
            ?? throw new InvalidOperationException("Main setup input missing.");
    }
}
