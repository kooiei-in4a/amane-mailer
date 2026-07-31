using System.Security.Cryptography;
using Amane.Mailer.Configuration;
using Amane.Mailer.Operations;
using Amane.Mailer.Operations.AcsSetup;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Setup.Assistant.Terminal;

/// <summary>
/// Interactive wizard flow for terminal Easy Setup (issue #453).
/// </summary>
internal sealed class SetupTerminalWizard
{
    private readonly ISetupTerminalConsole _console;
    private readonly ISetupAssistantOperations _operations;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly CancellationToken _externalCancellation;
    private readonly SetupTerminalLifetime _lifetime;
    private readonly List<IDisposable> _secrets = [];

    private SetupMode? _mode;
    private Guid _tenantId = Guid.CreateVersion7();
    private string _sessionId = NewSessionId();
    private string _tenantName = string.Empty;
    private string _sourceService = string.Empty;
    private string _senderEmail = string.Empty;
    private string _senderDisplayName = string.Empty;
    private SetupAssistantSecret? _serviceToken;
    private SetupAssistantSecret? _acsConnectionString;
    private SetupAssistantSecret? _acsConnectionStringConfirmation;
    private SetupAssistantSecret? _adminPassword;
    private SetupAssistantDockerPreflightOutcome? _dockerPreflight;
    private ISetupAssistantMainWorkflowState? _workflow;
    private SetupAssistantAdminPreflightOutcome? _adminPreflight;
    private SetupAssistantAdminBootstrapOutcome? _adminBootstrap;
    private bool _adminUnexpectedFailure;
    private SetupAssistantAdminProfile _adminProfile;
    private string _adminOriginText = string.Empty;
    private string _adminEnvironmentName = string.Empty;
    private string _adminAllowedLocalAddress = string.Empty;
    private bool _adminLoopbackOnlyPublished;
    private bool _adminApprovedReverseProxy;
    private bool _adminServerLocalAddressConfirmed;
    private string _adminUsername = string.Empty;

    private bool _mainSideEffectsStarted;
    private bool _adminSideEffectsStarted;
    private SetupTerminalMainSetupStatus _mainSetupStatus = SetupTerminalMainSetupStatus.NotStarted;
    private SetupTerminalAdminBootstrapStatus _adminBootstrapStatus =
        SetupTerminalAdminBootstrapStatus.NotRequested;

    internal SetupTerminalWizard(
        ISetupTerminalConsole console,
        ISetupAssistantOperations operations,
        TextWriter output,
        TextWriter error,
        CancellationToken externalCancellation,
        SetupTerminalLifetime? lifetime = null)
    {
        _console = console;
        _operations = operations;
        _output = output;
        _error = error;
        _externalCancellation = externalCancellation;
        _lifetime = lifetime ?? new SetupTerminalLifetime();
    }

    internal async Task<int> RunAsync()
    {
        using var lifetimeScope = _lifetime;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _externalCancellation,
            lifetimeScope.Token);

        try
        {
            WriteWelcome();
            if (!await RunDockerPreflightAsync(linked.Token))
            {
                WriteFinalSummary();
                return MapExitCode();
            }

            if (!SelectMode(linked.Token))
            {
                WriteFinalSummary();
                return MapExitCode();
            }

            if (_mode is null)
            {
                WriteFinalSummary();
                return MapExitCode();
            }

            if (!CollectTenantBasics(linked.Token)
                || !CollectProviderSettings(linked.Token)
                || !CollectAcsSettingsIfNeeded(linked.Token))
            {
                WriteFinalSummary();
                return MapExitCode();
            }

            if (!await RunMainSetupAsync(linked.Token))
            {
                WriteFinalSummary();
                return MapExitCode();
            }

            if (!IsMainSetupCompleatable())
            {
                _mainSetupStatus = SetupTerminalMainSetupStatus.Failed;
                WriteFinalSummary();
                return MapExitCode();
            }

            _mainSetupStatus = SetupTerminalMainSetupStatus.Succeeded;
            WriteMainSetupComplete();

            if (!await RunOptionalAdminBootstrapAsync(linked.Token))
            {
                WriteFinalSummary();
                return MapExitCode();
            }

            WriteFinalSummary();
            return MapExitCode();
        }
        catch (SecretOperationException ex) when (
            ex.CanonicalCode == AdminProviderRegisterAcsResultCodes.RejectedInputRedirected)
        {
            _error.WriteLine("setup assistant: terminal mode requires an interactive TTY.");
            _mainSetupStatus = SetupTerminalMainSetupStatus.Failed;
            return SetupAssistantCommand.FailureExitCode;
        }
        catch (SecretOperationException)
        {
            return HandleOperatorCancel();
        }
        catch (OperationCanceledException) when (_externalCancellation.IsCancellationRequested)
        {
            return HandleOperatorCancel();
        }
        catch (OperationCanceledException)
        {
            _error.WriteLine($"setup assistant: {_lifetime.DescribeStopReason()}");
            return HandleTimeoutCancel();
        }
        finally
        {
            DiscardAllSecrets();
        }
    }

    private void WriteWelcome()
    {
        Touch();
        _output.WriteLine("Amane Mailer Easy Setup（Terminal）");
        _output.WriteLine();
        _output.WriteLine("この Assistant は host 上で対話的に Main setup（mode 1〜4）と任意の Admin bootstrap を実行します。");
        _output.WriteLine("通常の Mailer runtime とは分離して動作します。");
        _output.WriteLine();
        _output.WriteLine("対応範囲:");
        _output.WriteLine("- mode 1〜4 の Main setup");
        _output.WriteLine("- Main setup 成功後の任意 Admin bootstrap");
        _output.WriteLine();
        _output.WriteLine("対応しないこと:");
        _output.WriteLine("- mode 5（production ACS + Queue）の自動設定（Manual runbook 案内のみ）");
        _output.WriteLine("- reverse proxy・証明書・DNS の自動構築");
        _output.WriteLine("- 実送信による運用確認の記録");
        _output.WriteLine();
        _output.WriteLine("各プロンプトで cancel と入力すると、Main setup の副作用開始前は clean cancel（exit 0）、");
        _output.WriteLine("Main setup の副作用開始後は exit 130 で終了します。Ctrl+C も同様です。");
        _output.WriteLine("Main setup 成功後の Admin bootstrap の cancel は Main 成功を維持し exit 0 です。");
        _output.WriteLine();
        WaitForContinue();
    }

    private async Task<bool> RunDockerPreflightAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Touch();
            _output.WriteLine();
            _output.WriteLine("=== Docker preflight ===");
            _output.WriteLine("Docker CLI と Compose plugin を確認します。");

            _lifetime.BeginOperation();
            SetupAssistantDockerPreflightOutcome preflight;
            try
            {
                preflight = await _operations.CheckDockerAsync(cancellationToken);
            }
            finally
            {
                _lifetime.EndOperation();
            }

            _dockerPreflight = preflight;
            SetupTerminalPresenter.WriteDockerPreflight(_output, preflight);
            if (preflight.Passed)
            {
                if (PromptContinueOrCancel("Setup mode 選択へ進みますか？ [y/n/cancel]: "))
                {
                    return true;
                }

                return false;
            }

            if (!PromptRetryOrCancel("Docker preflight を再実行しますか？ [y/n/cancel]: "))
            {
                return false;
            }
        }
    }

    private bool SelectMode(CancellationToken cancellationToken)
    {
        while (true)
        {
            Touch();
            cancellationToken.ThrowIfCancellationRequested();
            _output.WriteLine();
            _output.WriteLine("=== Setup mode 選択 ===");
            _output.WriteLine("1) local-mailpit — mode 1: Local Mailpit（develop・実送信なし）");
            _output.WriteLine("2) staging-no-send — mode 2: Staging ACS（送信検証を行わない）");
            _output.WriteLine("3) staging-verification — mode 3: Staging ACS（Staging verification）");
            _output.WriteLine("4) production-acs — mode 4: Production ACS（live_sending 有効化まで）");
            _output.WriteLine("5) production-queue — mode 5: Manual runbook 案内のみ");
            _output.WriteLine("cancel — 中止");

            var raw = _console.ReadLine("選択 (1-5 / cancel): ").Trim();
            if (IsCancelInput(raw))
            {
                return false;
            }

            var wire = raw switch
            {
                "1" => "local-mailpit",
                "2" => "staging-no-send",
                "3" => "staging-verification",
                "4" => "production-acs",
                "5" => SetupAssistantInputs.ManualModeValue,
                _ when SetupAssistantInputs.TryParseAutomatableMode(raw, out _) => raw,
                _ when string.Equals(raw, SetupAssistantInputs.ManualModeValue, StringComparison.Ordinal)
                    => SetupAssistantInputs.ManualModeValue,
                _ => string.Empty,
            };

            if (string.IsNullOrEmpty(wire))
            {
                _console.WriteError("選択できない mode です。");
                continue;
            }

            if (string.Equals(wire, SetupAssistantInputs.ManualModeValue, StringComparison.Ordinal))
            {
                _output.WriteLine();
                SetupTerminalPresenter.WriteManualModeGuidance(_output);
                _mainSetupStatus = SetupTerminalMainSetupStatus.CancelledClean;
                return false;
            }

            if (!SetupAssistantInputs.TryParseAutomatableMode(wire, out var mode))
            {
                SetupTerminalPresenter.WriteRejection(
                    _output,
                    SetupAssistantRejection.ModeNotSelectable);
                continue;
            }

            _mode = mode;
            _tenantId = Guid.CreateVersion7();
            _sessionId = NewSessionId();
            _output.WriteLine($"選択: {SetupModeParser.ToWireValue(mode)}");
            return true;
        }
    }

    private bool CollectTenantBasics(CancellationToken cancellationToken)
    {
        Touch();
        cancellationToken.ThrowIfCancellationRequested();
        var mode = _mode!.Value;
        _output.WriteLine();
        _output.WriteLine("=== Tenant 基本設定 ===");
        _output.WriteLine($"Setup mode: {SetupModeParser.ToWireValue(mode)}");
        _output.WriteLine($"environment: {SetupAssistantInputs.EnvironmentFor(mode)}");
        _output.WriteLine($"tenant_id: {_tenantId:D}");

        while (true)
        {
            _tenantName = _console.ReadLine("Tenant 名: ").Trim();
            if (IsCancelInput(_tenantName))
            {
                return false;
            }

            _sourceService = _console.ReadLine("Source service 名: ").Trim();
            if (IsCancelInput(_sourceService))
            {
                return false;
            }

            _senderEmail = _console.ReadLine("既定の送信元アドレス: ").Trim();
            if (IsCancelInput(_senderEmail))
            {
                return false;
            }

            _senderDisplayName = _console.ReadLine("送信元表示名: ").Trim();
            if (IsCancelInput(_senderDisplayName))
            {
                return false;
            }

            if (!SetupAssistantInputs.IsIdentifier(_tenantName)
                || !SetupAssistantInputs.IsSourceService(_sourceService))
            {
                SetupTerminalPresenter.WriteRejection(_output, SetupAssistantRejection.InvalidIdentifier);
                continue;
            }

            if (!SetupAssistantInputs.IsEmail(_senderEmail))
            {
                SetupTerminalPresenter.WriteRejection(_output, SetupAssistantRejection.InvalidEmail);
                continue;
            }

            if (!SetupAssistantInputs.IsDisplayText(_senderDisplayName))
            {
                SetupTerminalPresenter.WriteRejection(_output, SetupAssistantRejection.MissingRequiredField);
                continue;
            }

            _output.WriteLine($"送信元（マスク表示）: {SetupAssistantInputs.Mask(_senderEmail)}");
            return true;
        }
    }

    private bool CollectProviderSettings(CancellationToken cancellationToken)
    {
        Touch();
        cancellationToken.ThrowIfCancellationRequested();
        _output.WriteLine();
        _output.WriteLine("=== Provider 設定 ===");
        _output.WriteLine($"token env: {SetupAssistantInputs.TokenEnvFor(_mode!.Value)}");

        while (true)
        {
            var token = _console.ReadSecret("Service token: ");
            if (IsCancelInput(token))
            {
                return false;
            }

            var confirmation = _console.ReadSecret("Service token（確認）: ");
            if (IsCancelInput(confirmation))
            {
                return false;
            }

            if (!SetupAssistantInputs.IsSecret(token))
            {
                SetupTerminalPresenter.WriteRejection(_output, SetupAssistantRejection.SecretTooShort);
                continue;
            }

            if (!string.Equals(token, confirmation, StringComparison.Ordinal))
            {
                SetupTerminalPresenter.WriteRejection(_output, SetupAssistantRejection.SecretMismatch);
                continue;
            }

            _serviceToken = ReplaceSecret(_serviceToken, token);
            return true;
        }
    }

    private bool CollectAcsSettingsIfNeeded(CancellationToken cancellationToken)
    {
        if (_mode == SetupMode.LocalMailpit)
        {
            return true;
        }

        Touch();
        cancellationToken.ThrowIfCancellationRequested();
        _output.WriteLine();
        _output.WriteLine("=== ACS 設定 ===");

        while (true)
        {
            var connectionString = _console.ReadSecret("ACS connection string: ");
            if (IsCancelInput(connectionString))
            {
                return false;
            }

            var confirmation = _console.ReadSecret("ACS connection string（確認）: ");
            if (IsCancelInput(confirmation))
            {
                return false;
            }

            if (!SetupAssistantInputs.IsSecret(connectionString))
            {
                SetupTerminalPresenter.WriteRejection(_output, SetupAssistantRejection.SecretTooShort);
                continue;
            }

            if (!string.Equals(connectionString, confirmation, StringComparison.Ordinal))
            {
                SetupTerminalPresenter.WriteRejection(_output, SetupAssistantRejection.SecretMismatch);
                continue;
            }

            var displayName = _console.ReadLine("Platform sender 表示名: ").Trim();
            if (IsCancelInput(displayName))
            {
                return false;
            }

            if (!SetupAssistantInputs.IsDisplayText(displayName))
            {
                SetupTerminalPresenter.WriteRejection(_output, SetupAssistantRejection.MissingRequiredField);
                continue;
            }

            _acsConnectionString = ReplaceSecret(_acsConnectionString, connectionString);
            _acsConnectionStringConfirmation = ReplaceSecret(_acsConnectionStringConfirmation, confirmation);
            _senderDisplayName = displayName;
            return true;
        }
    }

    private async Task<bool> RunMainSetupAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Touch();
            if (!CollectApplyConfirmation(out var environmentConfirmation, out var intentConfirmation, cancellationToken))
            {
                return false;
            }

            string? stagingRecipient = null;
            string? stagingEnvironmentConfirmation = null;
            string? stagingIntentConfirmation = null;
            string? productionEnvironmentConfirmation = null;
            string? liveSendingApproval = null;

            if (_mode == SetupMode.StagingVerification)
            {
                if (!CollectStagingVerificationInput(
                        out stagingRecipient,
                        out stagingEnvironmentConfirmation,
                        out stagingIntentConfirmation,
                        cancellationToken))
                {
                    return false;
                }
            }
            else if (_mode == SetupMode.ProductionAcs)
            {
                if (!CollectLiveSendingInput(
                        out productionEnvironmentConfirmation,
                        out liveSendingApproval,
                        cancellationToken))
                {
                    return false;
                }
            }

            _mainSideEffectsStarted = true;

            var initial = SetupAssistantMainSetupOrchestrator.CreateInitial(_mode!.Value);
            if (_dockerPreflight is { Passed: true })
            {
                initial = SetupAssistantMainSetupOrchestrator.AcknowledgeDockerPreflight(
                    initial,
                    _dockerPreflight);
            }

            var collected = BuildCollectedInput(
                environmentConfirmation,
                intentConfirmation,
                stagingRecipient,
                stagingEnvironmentConfirmation,
                stagingIntentConfirmation,
                productionEnvironmentConfirmation,
                liveSendingApproval);

            _output.WriteLine();
            _output.WriteLine("=== Main setup 実行中 ===");
            _output.WriteLine("bundle の生成・適用と mode 固有の follow-up を実行します。");

            _lifetime.BeginOperation();
            SetupAssistantMainWorkflowTransition result;
            try
            {
                result = await SetupAssistantMainSetupOrchestrator.RunToCompletionAsync(
                    _operations,
                    initial,
                    collected,
                    cancellationToken);
            }
            finally
            {
                _lifetime.EndOperation();
            }

            _workflow = result.State;

            _output.WriteLine();
            SetupTerminalPresenter.WriteMainSetupResult(_output, result);

            if (ConfigurationStageSucceeded)
            {
                DiscardApplySecrets();
            }

            if (IsMainSetupCompleatable())
            {
                return true;
            }

            if (_workflow.CanRetryApply)
            {
                if (PromptRetryOrCancel("適用をやり直しますか？ [y/n/cancel]: "))
                {
                    ResetMainSetupState();
                    continue;
                }

                _mainSetupStatus = SetupTerminalMainSetupStatus.Failed;
                return true;
            }

            if (await TryRetryFollowUpAsync(cancellationToken))
            {
                if (IsMainSetupCompleatable())
                {
                    return true;
                }
            }

            _mainSetupStatus = SetupTerminalMainSetupStatus.Failed;
            return true;
        }
    }

    private void ResetMainSetupState()
    {
        _mainSideEffectsStarted = false;
        _workflow = null;
    }

    private async Task<bool> TryRetryFollowUpAsync(CancellationToken cancellationToken)
    {
        if (_workflow is null)
        {
            return false;
        }

        if (_mode == SetupMode.StagingVerification && _workflow.CanRetryStaging)
        {
            if (!PromptRetryOrCancel("Staging verification をやり直しますか？ [y/n/cancel]: "))
            {
                return false;
            }

            if (!CollectStagingVerificationInput(
                    out var recipient,
                    out var environmentConfirmation,
                    out var intentConfirmation,
                    cancellationToken))
            {
                return false;
            }

            return await RunStagingFollowUpAsync(
                recipient,
                environmentConfirmation,
                intentConfirmation,
                cancellationToken);
        }

        if (_mode == SetupMode.ProductionAcs && _workflow.CanRunLiveSending)
        {
            if (!PromptRetryOrCancel("live_sending 有効化をやり直しますか？ [y/n/cancel]: "))
            {
                return false;
            }

            if (!CollectLiveSendingInput(
                    out var productionEnvironmentConfirmation,
                    out var liveSendingApproval,
                    cancellationToken))
            {
                return false;
            }

            return await RunLiveSendingFollowUpAsync(
                productionEnvironmentConfirmation,
                liveSendingApproval,
                cancellationToken);
        }

        return false;
    }

    private async Task<bool> RunStagingFollowUpAsync(
        string recipient,
        string environmentConfirmation,
        string intentConfirmation,
        CancellationToken cancellationToken)
    {
        if (_workflow is null)
        {
            return false;
        }

        _mainSideEffectsStarted = true;
        _lifetime.BeginOperation();
        try
        {
            var result = await SetupAssistantMainSetupOrchestrator.AdvanceAsync(
                _operations,
                _workflow,
                new SetupAssistantMainCollectedInput
                {
                    TenantId = _tenantId,
                    StagingRecipientEmail = recipient,
                    StagingEnvironmentConfirmation = environmentConfirmation,
                    StagingIntentConfirmation = intentConfirmation,
                    AssistantSessionId = _sessionId,
                },
                cancellationToken);
            if (result.Rejected)
            {
                return false;
            }

            _workflow = result.State;
            _output.WriteLine();
            if (result.State.Staging is { } staging)
            {
                SetupTerminalPresenter.WriteStagingOutcome(_output, staging);
            }

            return result.State.IsComplete;
        }
        finally
        {
            _lifetime.EndOperation();
        }
    }

    private async Task<bool> RunLiveSendingFollowUpAsync(
        string productionEnvironmentConfirmation,
        string liveSendingApproval,
        CancellationToken cancellationToken)
    {
        if (_workflow is null)
        {
            return false;
        }

        _mainSideEffectsStarted = true;
        _lifetime.BeginOperation();
        try
        {
            var result = await SetupAssistantMainSetupOrchestrator.AdvanceAsync(
                _operations,
                _workflow,
                new SetupAssistantMainCollectedInput
                {
                    TenantId = _tenantId,
                    ProductionEnvironmentConfirmation = productionEnvironmentConfirmation,
                    LiveSendingEnableApproval = liveSendingApproval,
                },
                cancellationToken);
            if (result.Rejected)
            {
                return false;
            }

            _workflow = result.State;
            _output.WriteLine();
            if (result.State.LiveSending is { } liveSending)
            {
                SetupTerminalPresenter.WriteMainSetupOutcome(_output, liveSending);
            }

            return result.State.IsComplete;
        }
        finally
        {
            _lifetime.EndOperation();
        }
    }

    private bool CollectApplyConfirmation(
        out string environmentConfirmation,
        out string intentConfirmation,
        CancellationToken cancellationToken)
    {
        environmentConfirmation = string.Empty;
        intentConfirmation = string.Empty;
        Touch();
        cancellationToken.ThrowIfCancellationRequested();
        _output.WriteLine();
        _output.WriteLine("=== 適用前確認 ===");

        if (_mode == SetupMode.LocalMailpit)
        {
            _output.WriteLine("Local Mailpit mode では確認フレーズは不要です。");
            return PromptContinueOrCancel("設定 bundle の適用を実行しますか？ [y/n/cancel]: ");
        }

        var expectedEnvironment = SetupAssistantInputs.EnvironmentConfirmationFor(_mode!.Value);
        _output.WriteLine("次の 2 つのフレーズを大文字・小文字を含めて正確に入力してください。");
        environmentConfirmation = _console.ReadLine($"環境確認フレーズ（{expectedEnvironment}）: ").Trim();
        if (IsCancelInput(environmentConfirmation))
        {
            return false;
        }

        intentConfirmation = _console.ReadLine($"実行意図フレーズ（{AcsRegisterOperation.IntentPhrase}）: ").Trim();
        if (IsCancelInput(intentConfirmation))
        {
            return false;
        }

        if (!string.Equals(environmentConfirmation, expectedEnvironment, StringComparison.Ordinal)
            || !string.Equals(intentConfirmation, AcsRegisterOperation.IntentPhrase, StringComparison.Ordinal))
        {
            SetupTerminalPresenter.WriteRejection(_output, SetupAssistantRejection.ConfirmationPhraseMismatch);
            return CollectApplyConfirmation(out environmentConfirmation, out intentConfirmation, cancellationToken);
        }

        return true;
    }

    private bool CollectStagingVerificationInput(
        out string recipient,
        out string environmentConfirmation,
        out string intentConfirmation,
        CancellationToken cancellationToken)
    {
        recipient = string.Empty;
        environmentConfirmation = string.Empty;
        intentConfirmation = string.Empty;
        Touch();
        cancellationToken.ThrowIfCancellationRequested();
        _output.WriteLine();
        _output.WriteLine("=== Staging verification ===");
        _output.WriteLine("テスト送信の宛先は session 内でのみ保持し、マスク表示のみ行います。");

        recipient = _console.ReadSensitiveLine("テスト送信の宛先: ").Trim();
        if (IsCancelInput(recipient))
        {
            return false;
        }

        environmentConfirmation = _console.ReadLine(
            $"環境確認フレーズ（{AcsEnvironmentConfirmation.Staging}）: ").Trim();
        if (IsCancelInput(environmentConfirmation))
        {
            return false;
        }

        intentConfirmation = _console.ReadLine(
            $"実行意図フレーズ（{AcsStagingVerificationOperation.IntentPhrase}）: ").Trim();
        if (IsCancelInput(intentConfirmation))
        {
            return false;
        }

        if (!SetupAssistantInputs.IsEmail(recipient))
        {
            SetupTerminalPresenter.WriteRejection(_output, SetupAssistantRejection.InvalidEmail);
            return CollectStagingVerificationInput(
                out recipient,
                out environmentConfirmation,
                out intentConfirmation,
                cancellationToken);
        }

        if (!string.Equals(environmentConfirmation, AcsEnvironmentConfirmation.Staging, StringComparison.Ordinal)
            || !string.Equals(intentConfirmation, AcsStagingVerificationOperation.IntentPhrase, StringComparison.Ordinal))
        {
            SetupTerminalPresenter.WriteRejection(_output, SetupAssistantRejection.ConfirmationPhraseMismatch);
            return CollectStagingVerificationInput(
                out recipient,
                out environmentConfirmation,
                out intentConfirmation,
                cancellationToken);
        }

        _output.WriteLine($"宛先（マスク表示）: {SetupAssistantInputs.Mask(recipient)}");
        return true;
    }

    private bool CollectLiveSendingInput(
        out string productionEnvironmentConfirmation,
        out string liveSendingApproval,
        CancellationToken cancellationToken)
    {
        productionEnvironmentConfirmation = string.Empty;
        liveSendingApproval = string.Empty;
        Touch();
        cancellationToken.ThrowIfCancellationRequested();
        _output.WriteLine();
        _output.WriteLine("=== Production live_sending 有効化 ===");
        _output.WriteLine("live_sending を有効化します。実送信はこの Assistant では行いません。");

        productionEnvironmentConfirmation = _console.ReadLine(
            $"環境確認フレーズ（{AcsEnvironmentConfirmation.Production}）: ").Trim();
        if (IsCancelInput(productionEnvironmentConfirmation))
        {
            return false;
        }

        liveSendingApproval = _console.ReadLine(
            $"有効化承認フレーズ（{AcsLiveSendingApproval.EnablePhrase}）: ").Trim();
        if (IsCancelInput(liveSendingApproval))
        {
            return false;
        }

        if (!string.Equals(productionEnvironmentConfirmation, AcsEnvironmentConfirmation.Production, StringComparison.Ordinal)
            || !string.Equals(liveSendingApproval, AcsLiveSendingApproval.EnablePhrase, StringComparison.Ordinal))
        {
            SetupTerminalPresenter.WriteRejection(_output, SetupAssistantRejection.ConfirmationPhraseMismatch);
            return CollectLiveSendingInput(
                out productionEnvironmentConfirmation,
                out liveSendingApproval,
                cancellationToken);
        }

        return true;
    }

    private SetupAssistantMainCollectedInput BuildCollectedInput(
        string environmentConfirmation,
        string intentConfirmation,
        string? stagingRecipient,
        string? stagingEnvironmentConfirmation,
        string? stagingIntentConfirmation,
        string? productionEnvironmentConfirmation,
        string? liveSendingApproval) =>
        new()
        {
            MainSetupInput = BuildMainSetupInput(environmentConfirmation, intentConfirmation),
            TenantId = _tenantId,
            StagingRecipientEmail = stagingRecipient,
            StagingEnvironmentConfirmation = stagingEnvironmentConfirmation,
            StagingIntentConfirmation = stagingIntentConfirmation,
            AssistantSessionId = _sessionId,
            ProductionEnvironmentConfirmation = productionEnvironmentConfirmation,
            LiveSendingEnableApproval = liveSendingApproval,
        };

    private SetupAssistantMainSetupInput BuildMainSetupInput(
        string environmentConfirmation,
        string intentConfirmation)
    {
        var mode = _mode!.Value;
        var tokenEnv = SetupAssistantInputs.TokenEnvFor(mode);
        var tenants = new MailerTenantsFile
        {
            Version = 1,
            Environment = SetupAssistantInputs.EnvironmentFor(mode),
            Tenants =
            [
                new MailerTenant
                {
                    TenantId = _tenantId,
                    Name = _tenantName,
                    SourceServices = [_sourceService],
                    DefaultFrom = new MailerAddress
                    {
                        Email = _senderEmail,
                        DisplayName = _senderDisplayName,
                    },
                    TokenEnv = tokenEnv,
                    Provider = SetupAssistantInputs.ProviderFor(mode),
                    LiveSending = false,
                    Retry = new MailerRetryOptions
                    {
                        MaxAttempts = 5,
                        InitialDelaySeconds = 5,
                        MaxDelaySeconds = 300,
                    },
                },
            ],
        };

        return new SetupAssistantMainSetupInput
        {
            Mode = mode,
            Tenants = tenants,
            TokenSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [tokenEnv] = _serviceToken!.Reveal(),
            },
            AcsConnectionString = _acsConnectionString?.Reveal(),
            AcsConnectionStringConfirmation = _acsConnectionStringConfirmation?.Reveal()
                ?? _acsConnectionString?.Reveal(),
            PlatformSender = mode == SetupMode.LocalMailpit
                ? null
                : new SetupPlatformSenderInput
                {
                    Environment = SetupAssistantInputs.EnvironmentFor(mode),
                    Email = _senderEmail,
                    DisplayName = _senderDisplayName,
                },
            EnvironmentConfirmation = environmentConfirmation,
            IntentConfirmation = intentConfirmation,
        };
    }

    private void WriteMainSetupComplete()
    {
        _output.WriteLine();
        _output.WriteLine("=== Main setup 完了 ===");
        _output.WriteLine("Main setup は完了しました。");
        _output.WriteLine($"設定の適用: {(ConfigurationStageSucceeded ? "適用済み" : "未適用")}");
        _output.WriteLine($"Staging verification: {DescribeStagingState()}");
        _output.WriteLine($"Deployment send-ready: {(DeploymentSendReady ? "到達" : "未到達")}");
        _output.WriteLine("実送信による運用確認: 記録していません。必要な場合は Manual verification を実施してください。");
        _output.WriteLine("Production mode の通常完了は send-ready 到達までです。deployment の運用確認やリリース判定は記録していません。");
    }

    private async Task<bool> RunOptionalAdminBootstrapAsync(CancellationToken cancellationToken)
    {
        Touch();
        _output.WriteLine();
        _output.WriteLine("=== Admin を有効化するか選択 ===");
        _output.WriteLine("Admin は既定で無効です。有効化は Main setup とは独立した任意の transaction です。");
        _output.WriteLine("Admin bootstrap が失敗しても Main setup の成功は取り消されません。");

        try
        {
            if (!PromptYesNoOrCancel("Admin を有効化しますか？ [y/n/cancel]: ", out var enableAdmin))
            {
                _adminBootstrapStatus = SetupTerminalAdminBootstrapStatus.Cancelled;
                return true;
            }

            if (!enableAdmin)
            {
                _adminBootstrapStatus = SetupTerminalAdminBootstrapStatus.Declined;
                return true;
            }

            if (!CollectAdminAccessPreflight(cancellationToken))
            {
                _adminBootstrapStatus = SetupTerminalAdminBootstrapStatus.Cancelled;
                return true;
            }

            _lifetime.BeginOperation();
            try
            {
                _adminPreflight = await _operations.CheckAdminAccessProfileAsync(
                    BuildAdminAccessInput(),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Side effects have not started; clean Admin cancel.
                _adminBootstrapStatus = SetupTerminalAdminBootstrapStatus.Cancelled;
                return true;
            }
            catch (Exception)
            {
                // Preflight threw unexpectedly: no Admin mutation; keep Main succeeded.
                _adminBootstrapStatus = SetupTerminalAdminBootstrapStatus.Failed;
                SetupTerminalPresenter.WriteAdminPreflightUnexpectedFailure(_output);
                return true;
            }
            finally
            {
                _lifetime.EndOperation();
            }

            _output.WriteLine();
            SetupTerminalPresenter.WriteAdminPreflight(_output, _adminPreflight!);
            if (_adminPreflight is not { Satisfied: true })
            {
                _adminBootstrapStatus = SetupTerminalAdminBootstrapStatus.Failed;
                return true;
            }

            if (!CollectAdminCredentials(cancellationToken))
            {
                _adminBootstrapStatus = SetupTerminalAdminBootstrapStatus.Cancelled;
                return true;
            }

            _adminSideEffectsStarted = true;
            _lifetime.BeginOperation();
            try
            {
                _adminBootstrap = await _operations.BootstrapAdminAsync(
                    new SetupAssistantAdminBootstrapInput
                    {
                        Access = BuildAdminAccessInput(),
                        Username = _adminUsername,
                        Password = _adminPassword!,
                        TenantIds = [_tenantId],
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // After Admin side effects may have started, cancellation is state-unknown.
                _adminUnexpectedFailure = true;
                _adminBootstrapStatus = SetupTerminalAdminBootstrapStatus.CancelledUnknown;
                SetupTerminalPresenter.WriteAdminCancelledUnknown(_output);
                return true;
            }
            catch (Exception)
            {
                _adminUnexpectedFailure = true;
                _adminBootstrapStatus = SetupTerminalAdminBootstrapStatus.Failed;
                SetupTerminalPresenter.WriteAdminUnexpectedFailure(_output);
                return true;
            }
            finally
            {
                _lifetime.EndOperation();
                DiscardAdminPassword();
            }

            _output.WriteLine();
            SetupTerminalPresenter.WriteAdminBootstrapOutcome(_output, _adminBootstrap!);
            _adminBootstrapStatus = _adminBootstrap!.Kind == SetupAssistantOutcomeKind.Succeeded
                ? SetupTerminalAdminBootstrapStatus.Succeeded
                : SetupTerminalAdminBootstrapStatus.Failed;
            return true;
        }
        catch (SecretOperationException) when (
            _mainSetupStatus == SetupTerminalMainSetupStatus.Succeeded
            || ConfigurationStageSucceeded)
        {
            // Input cancel after Main success is Admin-only.
            if (_adminSideEffectsStarted)
            {
                _adminUnexpectedFailure = true;
                _adminBootstrapStatus = SetupTerminalAdminBootstrapStatus.CancelledUnknown;
                SetupTerminalPresenter.WriteAdminCancelledUnknown(_output);
            }
            else
            {
                _adminBootstrapStatus = SetupTerminalAdminBootstrapStatus.Cancelled;
            }

            return true;
        }
        catch (Exception) when (
            _mainSetupStatus == SetupTerminalMainSetupStatus.Succeeded
            || ConfigurationStageSucceeded)
        {
            // Any other unexpected Admin-path exception must not unwind Main success.
            _adminUnexpectedFailure = true;
            _adminBootstrapStatus = _adminSideEffectsStarted
                ? SetupTerminalAdminBootstrapStatus.CancelledUnknown
                : SetupTerminalAdminBootstrapStatus.Failed;
            if (_adminSideEffectsStarted)
            {
                SetupTerminalPresenter.WriteAdminCancelledUnknown(_output);
            }
            else
            {
                SetupTerminalPresenter.WriteAdminPreflightUnexpectedFailure(_output);
            }

            return true;
        }
    }

    private bool CollectAdminAccessPreflight(CancellationToken cancellationToken)
    {
        Touch();
        cancellationToken.ThrowIfCancellationRequested();
        _output.WriteLine();
        _output.WriteLine("=== Admin access profile preflight ===");
        _output.WriteLine("1) local-development — loopback・HTTP 許可");
        _output.WriteLine("2) production-https — 承認済み reverse proxy が既に存在");

        while (true)
        {
            var raw = _console.ReadLine("Access profile (1/2/cancel): ").Trim();
            if (IsCancelInput(raw))
            {
                return false;
            }

            if (!TryParseAdminProfile(raw, out _adminProfile))
            {
                SetupTerminalPresenter.WriteRejection(_output, SetupAssistantRejection.AdminProfileNotSelectable);
                continue;
            }

            _adminOriginText = _console.ReadLine("Admin の接続元 URL: ").Trim();
            if (IsCancelInput(_adminOriginText))
            {
                return false;
            }

            _adminEnvironmentName = _console.ReadLine("ASPNETCORE_ENVIRONMENT: ").Trim();
            if (IsCancelInput(_adminEnvironmentName))
            {
                return false;
            }

            _adminAllowedLocalAddress = _console.ReadLine("Admin 到達を許可する server local address: ").Trim();
            if (IsCancelInput(_adminAllowedLocalAddress))
            {
                return false;
            }

            if (!SetupAssistantInputs.IsAbsoluteOrigin(_adminOriginText))
            {
                SetupTerminalPresenter.WriteRejection(_output, SetupAssistantRejection.InvalidOrigin);
                continue;
            }

            if (!SetupAssistantInputs.IsIdentifier(_adminEnvironmentName)
                || !SetupAssistantInputs.IsIpAddress(_adminAllowedLocalAddress))
            {
                SetupTerminalPresenter.WriteRejection(_output, SetupAssistantRejection.MissingRequiredField);
                continue;
            }

            if (!_console.TryReadYesNo(
                    "host へ公開しているのは loopback port だけである [y/n]: ",
                    out _adminLoopbackOnlyPublished)
                || !_console.TryReadYesNo(
                    "承認済み HTTPS reverse proxy 経路が既に存在する [y/n]: ",
                    out _adminApprovedReverseProxy)
                || !_console.TryReadYesNo(
                    "server 側 local address を確認済みである [y/n]: ",
                    out _adminServerLocalAddressConfirmed))
            {
                return false;
            }

            _output.WriteLine("Easy Setup は reverse proxy・証明書・DNS を構築しません。既存の経路のみを判定します。");
            return true;
        }
    }

    private bool CollectAdminCredentials(CancellationToken cancellationToken)
    {
        Touch();
        cancellationToken.ThrowIfCancellationRequested();
        _output.WriteLine();
        _output.WriteLine("=== Admin bootstrap ===");
        _output.WriteLine("Admin の初期認証情報を設定します。password は再表示しません。");

        while (true)
        {
            _adminUsername = _console.ReadLine("Admin ユーザー名: ").Trim();
            if (IsCancelInput(_adminUsername))
            {
                return false;
            }

            var password = _console.ReadSecret("Admin password: ");
            if (IsCancelInput(password))
            {
                return false;
            }

            var confirmation = _console.ReadSecret("Admin password（確認）: ");
            if (IsCancelInput(confirmation))
            {
                return false;
            }

            if (!SetupAssistantInputs.IsIdentifier(_adminUsername))
            {
                SetupTerminalPresenter.WriteRejection(_output, SetupAssistantRejection.InvalidIdentifier);
                continue;
            }

            if (!SetupAssistantInputs.IsSecret(password))
            {
                SetupTerminalPresenter.WriteRejection(_output, SetupAssistantRejection.SecretTooShort);
                continue;
            }

            if (!string.Equals(password, confirmation, StringComparison.Ordinal))
            {
                SetupTerminalPresenter.WriteRejection(_output, SetupAssistantRejection.SecretMismatch);
                continue;
            }

            _adminPassword = ReplaceSecret(_adminPassword, password);
            return true;
        }
    }

    private static bool TryParseAdminProfile(string raw, out SetupAssistantAdminProfile profile) =>
        raw switch
        {
            "1" or SetupAssistantInputs.LocalDevelopmentProfileValue =>
                AssignProfile(SetupAssistantAdminProfile.LocalDevelopment, out profile),
            "2" or SetupAssistantInputs.ProductionHttpsProfileValue =>
                AssignProfile(SetupAssistantAdminProfile.ProductionHttps, out profile),
            _ => AssignProfile(default, out profile) && false,
        };

    private static bool AssignProfile(SetupAssistantAdminProfile value, out SetupAssistantAdminProfile profile)
    {
        profile = value;
        return true;
    }

    private SetupAssistantAdminAccessInput BuildAdminAccessInput() =>
        new()
        {
            Profile = _adminProfile,
            OriginText = _adminOriginText,
            EnvironmentName = _adminEnvironmentName,
            AllowedLocalAddress = _adminAllowedLocalAddress,
            AllowHttp = _adminProfile == SetupAssistantAdminProfile.LocalDevelopment,
            LoopbackOnlyPublished = _adminLoopbackOnlyPublished,
            ApprovedReverseProxy = _adminApprovedReverseProxy,
            ServerLocalAddressConfirmed = _adminServerLocalAddressConfirmed,
        };

    private void WriteFinalSummary()
    {
        SetupTerminalPresenter.WriteFinalSummary(
            _output,
            new SetupTerminalRunSummary
            {
                MainSetupStatus = _mainSetupStatus,
                AdminBootstrapStatus = _adminBootstrapStatus,
                MainSetupSucceeded = ConfigurationStageSucceeded && IsMainSetupCompleatable(),
                DeploymentSendReady = DeploymentSendReady,
                Staging = _workflow?.Staging,
                AdminUnexpectedFailure = _adminUnexpectedFailure,
            });
    }

    private int MapExitCode()
    {
        // After Main success, Admin cancel/failure never becomes a non-zero process exit.
        if (_mainSetupStatus == SetupTerminalMainSetupStatus.Succeeded
            || (_mainSetupStatus != SetupTerminalMainSetupStatus.Failed
                && ConfigurationStageSucceeded
                && IsMainSetupCompleatable()))
        {
            return SetupAssistantCommand.SuccessExitCode;
        }

        if (_mainSetupStatus == SetupTerminalMainSetupStatus.Cancelled && _mainSideEffectsStarted)
        {
            return SetupTerminalAssistant.CancelledMidOperationExitCode;
        }

        if (_mainSetupStatus == SetupTerminalMainSetupStatus.Failed)
        {
            return SetupAssistantCommand.FailureExitCode;
        }

        return SetupAssistantCommand.SuccessExitCode;
    }

    private int HandleOperatorCancel()
    {
        if (_mainSetupStatus == SetupTerminalMainSetupStatus.Succeeded
            || (ConfigurationStageSucceeded && IsMainSetupCompleatable()))
        {
            // Main already succeeded: Admin-only cancel.
            _mainSetupStatus = SetupTerminalMainSetupStatus.Succeeded;
            if (_adminSideEffectsStarted)
            {
                _adminUnexpectedFailure = true;
                _adminBootstrapStatus = SetupTerminalAdminBootstrapStatus.CancelledUnknown;
            }
            else if (_adminBootstrapStatus == SetupTerminalAdminBootstrapStatus.NotRequested)
            {
                _adminBootstrapStatus = SetupTerminalAdminBootstrapStatus.Cancelled;
            }

            _error.WriteLine(_lifetime.StopReason == SetupAssistantShutdownReason.None
                ? "setup assistant: cancelled."
                : $"setup assistant: {_lifetime.DescribeStopReason()}");
            WriteFinalSummary();
            return SetupAssistantCommand.SuccessExitCode;
        }

        if (_mainSideEffectsStarted)
        {
            _mainSetupStatus = SetupTerminalMainSetupStatus.Cancelled;
            if (_adminSideEffectsStarted
                && _adminBootstrapStatus == SetupTerminalAdminBootstrapStatus.NotRequested)
            {
                _adminBootstrapStatus = SetupTerminalAdminBootstrapStatus.Cancelled;
            }
        }
        else
        {
            _mainSetupStatus = SetupTerminalMainSetupStatus.CancelledClean;
        }

        _error.WriteLine(_lifetime.StopReason == SetupAssistantShutdownReason.None
            ? "setup assistant: cancelled."
            : $"setup assistant: {_lifetime.DescribeStopReason()}");
        WriteFinalSummary();
        return MapExitCode();
    }

    private int HandleTimeoutCancel()
    {
        if (_mainSetupStatus == SetupTerminalMainSetupStatus.Succeeded
            || (ConfigurationStageSucceeded && IsMainSetupCompleatable()))
        {
            _mainSetupStatus = SetupTerminalMainSetupStatus.Succeeded;
            if (_adminSideEffectsStarted)
            {
                _adminUnexpectedFailure = true;
                _adminBootstrapStatus = SetupTerminalAdminBootstrapStatus.CancelledUnknown;
            }
            else if (_adminBootstrapStatus == SetupTerminalAdminBootstrapStatus.NotRequested)
            {
                _adminBootstrapStatus = SetupTerminalAdminBootstrapStatus.Cancelled;
            }

            WriteFinalSummary();
            return SetupAssistantCommand.SuccessExitCode;
        }

        if (_mainSideEffectsStarted)
        {
            _mainSetupStatus = SetupTerminalMainSetupStatus.Cancelled;
        }
        else
        {
            _mainSetupStatus = SetupTerminalMainSetupStatus.CancelledClean;
        }

        WriteFinalSummary();
        return MapExitCode();
    }

    private bool ConfigurationStageSucceeded =>
        _workflow?.ConfigurationStageSucceeded == true;

    private bool DeploymentSendReady =>
        _workflow?.DeploymentSendReady == true;

    private bool IsMainSetupCompleatable() =>
        _workflow?.IsComplete == true;

    private string DescribeStagingState() =>
        _workflow?.Staging is null
            ? "未実施"
            : _workflow.Staging.Kind == SetupAssistantOutcomeKind.Succeeded ? "送信要求受理" : "失敗";

    private void Touch()
    {
        _lifetime.Touch();
        _lifetime.EnsureNotExpired();
    }

    private void WaitForContinue()
    {
        Touch();
        var raw = _console.ReadLine("Enter で続行（cancel で中止）: ").Trim();
        if (IsCancelInput(raw))
        {
            throw new SecretOperationException(
                AdminProviderRegisterAcsResultCodes.RejectedCancelled,
                "Input was interrupted.");
        }
    }

    private bool PromptContinueOrCancel(string prompt) => PromptYesNoOrCancel(prompt, out var value) && value;

    private bool PromptRetryOrCancel(string prompt) => PromptYesNoOrCancel(prompt, out var value) && value;

    private bool PromptYesNoOrCancel(string prompt, out bool value)
    {
        while (true)
        {
            Touch();
            var raw = _console.ReadLine(prompt).Trim();
            if (IsCancelInput(raw))
            {
                value = false;
                return false;
            }

            if (string.Equals(raw, "y", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase))
            {
                value = true;
                return true;
            }

            if (string.Equals(raw, "n", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "no", StringComparison.OrdinalIgnoreCase))
            {
                value = false;
                return true;
            }

            _console.WriteError("y、n、または cancel で入力してください。");
        }
    }

    private static bool IsCancelInput(string raw) =>
        string.Equals(raw, "cancel", StringComparison.OrdinalIgnoreCase);

    private SetupAssistantSecret ReplaceSecret(SetupAssistantSecret? existing, string value)
    {
        if (existing is not null)
        {
            existing.Dispose();
            _secrets.Remove(existing);
        }

        var captured = SetupAssistantSecret.Capture(value);
        _secrets.Add(captured);
        return captured;
    }

    private void DiscardApplySecrets()
    {
        if (_serviceToken is not null)
        {
            _serviceToken.Dispose();
            _secrets.Remove(_serviceToken);
            _serviceToken = null;
        }

        if (_acsConnectionString is not null)
        {
            _acsConnectionString.Dispose();
            _secrets.Remove(_acsConnectionString);
            _acsConnectionString = null;
        }

        if (_acsConnectionStringConfirmation is not null)
        {
            _acsConnectionStringConfirmation.Dispose();
            _secrets.Remove(_acsConnectionStringConfirmation);
            _acsConnectionStringConfirmation = null;
        }
    }

    private void DiscardAdminPassword()
    {
        if (_adminPassword is null)
        {
            return;
        }

        _adminPassword.Dispose();
        _secrets.Remove(_adminPassword);
        _adminPassword = null;
    }

    private void DiscardAllSecrets()
    {
        DiscardApplySecrets();
        DiscardAdminPassword();
        foreach (var secret in _secrets)
        {
            secret.Dispose();
        }

        _secrets.Clear();
    }

    private static string NewSessionId() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
