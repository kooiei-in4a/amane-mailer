using System.Runtime.Versioning;
using System.Text.Json;
using Amane.Mailer.Operations.AcsSetup;
using Amane.Mailer.Setup;
using Amane.Mailer.Setup.Assistant;
using Amane.Mailer.Setup.NonInteractive;
using Amane.Mailer.Tests.Setup.Assistant;

namespace Amane.Mailer.Tests.Setup.NonInteractive;

public sealed class SetupApplyNonInteractiveCommandTests
{
    [Fact]
    public void TryParseArguments_without_required_flags_is_rejected()
    {
        Assert.False(
            SetupApplyNonInteractiveCommand.TryParseArguments(
                ["setup", "apply"],
                out _,
                out var error));
        Assert.Contains("Missing required", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParseArguments_rejects_missing_non_interactive_flag()
    {
        var config = Path.Combine(Path.GetTempPath(), "amane-setup-config.json");
        Assert.False(
            SetupApplyNonInteractiveCommand.TryParseArguments(
                ["setup", "apply", "--config", config],
                out _,
                out var error));
        Assert.Contains("Missing required", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Happy_path_emits_json_with_adminBootstrapPerformed_false()
    {
        var operations = new FakeSetupAssistantOperations();
        var configPath = SetupNonInteractiveTestSupport.WriteOwnerOnlyConfigOnHost(
            Path.Combine(Path.GetTempPath(), "amane-ni-" + Guid.NewGuid().ToString("N"), "config.json"),
            SetupNonInteractiveTestSupport.BuildLocalMailpitJson());

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await SetupApplyNonInteractiveCommand.ExecuteCoreAsync(
            configPath,
            stdout,
            stderr,
            CancellationToken.None,
            operations,
            new HostSetupFileSystem());

        Assert.Equal(SetupApplyNonInteractiveCommand.SuccessExitCode, exit);
        var result = SetupNonInteractiveTestSupport.DeserializeResult(stdout.ToString().Trim());
        Assert.True(result.Ok);
        Assert.False(result.AdminBootstrapPerformed);
        Assert.Equal(1, operations.DockerPreflightCalls);
        Assert.Equal(1, operations.ApplyCalls);
    }

    [Fact]
    public async Task Missing_tenant_rejects_before_side_effects()
    {
        var operations = new GuardSetupAssistantOperations();
        var json = SetupNonInteractiveTestSupport.BuildLocalMailpitJson()
            .Replace("\"tenant\":", "\"tenantX\":", StringComparison.Ordinal);
        var configPath = SetupNonInteractiveTestSupport.WriteOwnerOnlyConfigOnHost(
            Path.Combine(Path.GetTempPath(), "amane-ni-" + Guid.NewGuid().ToString("N"), "config.json"),
            json);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await SetupApplyNonInteractiveCommand.ExecuteCoreAsync(
            configPath,
            stdout,
            stderr,
            CancellationToken.None,
            operations,
            new HostSetupFileSystem());

        Assert.Equal(SetupApplyNonInteractiveCommand.FailureExitCode, exit);
        Assert.Equal(0, operations.CallCount);
    }

    [Fact]
    public async Task Unsafe_permissions_are_rejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var operations = new GuardSetupAssistantOperations();
        var configPath = SetupNonInteractiveTestSupport.WriteOwnerOnlyConfigOnHost(
            Path.Combine(Path.GetTempPath(), "amane-ni-" + Guid.NewGuid().ToString("N"), "config.json"),
            SetupNonInteractiveTestSupport.BuildLocalMailpitJson());
        WeakenWindowsAcl(configPath);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await SetupApplyNonInteractiveCommand.ExecuteCoreAsync(
            configPath,
            stdout,
            stderr,
            CancellationToken.None,
            operations,
            new HostSetupFileSystem());

        Assert.Equal(SetupApplyNonInteractiveCommand.FailureExitCode, exit);
        Assert.Equal(0, operations.CallCount);
        var result = SetupNonInteractiveTestSupport.DeserializeResult(stdout.ToString().Trim());
        Assert.Equal(SetupNonInteractiveResultCode.ConfigPermissionsRejected, result.Code);
    }

    [Fact]
    public async Task Symlink_config_path_is_rejected()
    {
        var operations = new GuardSetupAssistantOperations();
        var fileSystem = new MemorySetupFileSystem
        {
            InspectOverride = _ => SetupLinkInspectionResult.IsLinkOrReparse,
        };
        var configPath = Path.Combine(Path.GetTempPath(), $"amane-ni-{Guid.NewGuid():N}", "config.json");
        SetupNonInteractiveTestSupport.WriteOwnerOnlyConfigInMemory(
            fileSystem,
            configPath,
            SetupNonInteractiveTestSupport.BuildLocalMailpitJson());

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await SetupApplyNonInteractiveCommand.ExecuteCoreAsync(
            configPath,
            stdout,
            stderr,
            CancellationToken.None,
            operations,
            fileSystem);

        Assert.Equal(SetupApplyNonInteractiveCommand.FailureExitCode, exit);
        Assert.Equal(0, operations.CallCount);
        var result = SetupNonInteractiveTestSupport.DeserializeResult(stdout.ToString().Trim());
        Assert.Equal(SetupNonInteractiveResultCode.ConfigPathUnsafe, result.Code);
    }

    [Fact]
    public async Task Secrets_do_not_appear_on_stdout_or_stderr()
    {
        var operations = new FakeSetupAssistantOperations();
        var configPath = SetupNonInteractiveTestSupport.WriteOwnerOnlyConfigOnHost(
            Path.Combine(Path.GetTempPath(), "amane-ni-" + Guid.NewGuid().ToString("N"), "config.json"),
            SetupNonInteractiveTestSupport.BuildLocalMailpitJson());

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        _ = await SetupApplyNonInteractiveCommand.ExecuteCoreAsync(
            configPath,
            stdout,
            stderr,
            CancellationToken.None,
            operations,
            new HostSetupFileSystem());

        var combined = stdout.ToString() + stderr.ToString();
        Assert.DoesNotContain(SetupNonInteractiveTestSupport.SyntheticServiceToken, combined, StringComparison.Ordinal);
        Assert.DoesNotContain(SetupNonInteractiveTestSupport.SyntheticAcsConnectionString, combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_returns_cancelled_exit_code()
    {
        var operations = new FakeSetupAssistantOperations
        {
            ApplyHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var configPath = SetupNonInteractiveTestSupport.WriteOwnerOnlyConfigOnHost(
            Path.Combine(Path.GetTempPath(), "amane-ni-" + Guid.NewGuid().ToString("N"), "config.json"),
            SetupNonInteractiveTestSupport.BuildLocalMailpitJson());

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var cts = new CancellationTokenSource();
        var run = SetupApplyNonInteractiveCommand.ExecuteCoreAsync(
            configPath,
            stdout,
            stderr,
            cts.Token,
            operations,
            new HostSetupFileSystem());
        while (operations.ApplyCalls == 0)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();
        operations.ApplyHold!.TrySetResult();
        var exit = await run;

        Assert.Equal(SetupApplyNonInteractiveCommand.CancelledExitCode, exit);
        var result = SetupNonInteractiveTestSupport.DeserializeResult(stdout.ToString().Trim());
        Assert.Equal(SetupNonInteractiveResultCode.Cancelled, result.Code);
    }

    [Fact]
    public async Task Main_setup_failure_returns_failure_exit_code()
    {
        var operations = new FakeSetupAssistantOperations
        {
            MainSetup = new SetupAssistantMainSetupOutcome
            {
                Code = SetupApplyResultCode.FreshApplyFailed,
                Kind = SetupAssistantOutcomeKind.Failed,
                ConfigurationApplied = false,
            },
        };
        var configPath = SetupNonInteractiveTestSupport.WriteOwnerOnlyConfigOnHost(
            Path.Combine(Path.GetTempPath(), "amane-ni-" + Guid.NewGuid().ToString("N"), "config.json"),
            SetupNonInteractiveTestSupport.BuildLocalMailpitJson());

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await SetupApplyNonInteractiveCommand.ExecuteCoreAsync(
            configPath,
            stdout,
            stderr,
            CancellationToken.None,
            operations,
            new HostSetupFileSystem());

        Assert.Equal(SetupApplyNonInteractiveCommand.FailureExitCode, exit);
        var result = SetupNonInteractiveTestSupport.DeserializeResult(stdout.ToString().Trim());
        Assert.False(result.Ok);
    }

    [SupportedOSPlatform("windows")]
    private static void WeakenWindowsAcl(string path)
    {
        var fileInfo = new FileInfo(path);
        var security = fileInfo.GetAccessControl();
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            "Everyone",
            System.Security.AccessControl.FileSystemRights.Read,
            System.Security.AccessControl.AccessControlType.Allow));
        fileInfo.SetAccessControl(security);
    }

    private sealed class GuardSetupAssistantOperations : ISetupAssistantOperations
    {
        internal int CallCount { get; private set; }

        public Task<SetupAssistantDockerPreflightOutcome> CheckDockerAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("Docker must not run for this test.");
        }

        public Task<SetupAssistantMainSetupOutcome> ApplyMainSetupAsync(
            SetupAssistantMainSetupInput input,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("Apply must not run for this test.");
        }

        public Task<SetupAssistantStagingOutcome> VerifyStagingAsync(
            SetupAssistantStagingInput input,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SetupAssistantMainSetupOutcome> EnableLiveSendingAsync(
            SetupAssistantProductionInput input,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SetupAssistantAdminPreflightOutcome> CheckAdminAccessProfileAsync(
            SetupAssistantAdminAccessInput input,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SetupAssistantAdminBootstrapOutcome> BootstrapAdminAsync(
            SetupAssistantAdminBootstrapInput input,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
