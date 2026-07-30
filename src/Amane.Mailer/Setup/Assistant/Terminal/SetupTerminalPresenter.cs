using Amane.Mailer.Operations.AcsSetup;

namespace Amane.Mailer.Setup.Assistant.Terminal;

/// <summary>
/// Human-readable terminal output derived from the shared assistant result model. Never prints
/// secrets, raw provider errors, or private host paths.
/// </summary>
internal static class SetupTerminalPresenter
{
    internal static void WriteMainSetupResult(TextWriter output, SetupAssistantMainSetupRunResult result)
    {
        if (result.MainSetup is { } mainSetup)
        {
            WriteMainSetupOutcome(output, mainSetup);
        }
        else if (result.FailedStep == SetupAssistantMainSetupFailedStep.DockerPreflight)
        {
            WriteOutcomeHeader(output, SetupAssistantOutcomeKind.Failed);
            output.WriteLine(SetupAssistantResultPresenter.Describe(result.Code));
            WriteCodeRow(output, "結果コード", result.Code);
        }

        if (result.Staging is { } staging)
        {
            output.WriteLine();
            WriteStagingOutcome(output, staging);
        }

        if (result.LiveSending is { } liveSending)
        {
            output.WriteLine();
            WriteMainSetupOutcome(output, liveSending);
            output.WriteLine("live_sending の有効化は Main setup の適用結果とは独立して記録されます。");
        }
    }

    internal static void WriteMainSetupOutcome(TextWriter output, SetupAssistantMainSetupOutcome outcome)
    {
        WriteOutcomeHeader(output, outcome.Kind);
        output.WriteLine(SetupAssistantResultPresenter.Describe(outcome.Code));
        WriteCodeRow(output, "結果コード", outcome.Code);
        output.WriteLine($"設定の適用: {(outcome.ConfigurationApplied ? "適用済み" : "未適用")}");
        output.WriteLine($"Deployment send-ready: {(outcome.DeploymentSendReady ? "到達" : "未到達")}");
        var rollback = SetupAssistantResultPresenter.SafeLabel(outcome.ConfigRollbackStatus, "not-applicable");
        output.WriteLine($"設定の巻き戻し: {rollback}");
        if (outcome.PersistentSideEffectMayRemain)
        {
            var kind = SetupAssistantResultPresenter.SafeLabel(outcome.PersistentSideEffectKind, "unknown");
            output.WriteLine($"残存する副作用: {kind}");
        }

        var action = SetupAssistantResultPresenter.DescribeAction(outcome.ActionCode);
        if (!string.IsNullOrEmpty(action))
        {
            output.WriteLine($"必要な対応: {action}");
        }
    }

    internal static void WriteStagingOutcome(TextWriter output, SetupAssistantStagingOutcome staging)
    {
        WriteOutcomeHeader(output, staging.Kind);
        output.WriteLine(SetupAssistantResultPresenter.Describe(staging.Code));
        WriteCodeRow(output, "結果コード", staging.Code);
        output.WriteLine($"送信要求受理: {(staging.SendRequestAccepted ? "はい" : "いいえ")}");
        output.WriteLine(
            $"受信箱の確認: {SetupAssistantResultPresenter.SafeLabel(staging.MailboxCheckStatus, "未実施（手動確認が必要）")}");
        if (!string.IsNullOrEmpty(staging.MaskedSenderEmail))
        {
            output.WriteLine($"送信元（マスク表示）: {staging.MaskedSenderEmail}");
        }

        if (!string.IsNullOrEmpty(staging.MaskedRecipientEmail))
        {
            output.WriteLine($"宛先（マスク表示）: {staging.MaskedRecipientEmail}");
        }
    }

    internal static void WriteAdminBootstrapOutcome(TextWriter output, SetupAssistantAdminBootstrapOutcome outcome)
    {
        WriteOutcomeHeader(output, outcome.Kind);
        output.WriteLine(SetupAssistantResultPresenter.Describe(outcome.Code));
        WriteCodeRow(output, "結果コード", outcome.Code);
        output.WriteLine($"access profile: {SetupAssistantResultPresenter.SafeCode(outcome.AccessProfile)}");
        output.WriteLine($"設定の巻き戻し: {SetupAssistantResultPresenter.SafeCode(outcome.ConfigRollback)}");
        output.WriteLine($"Admin データベース状態: {SetupAssistantResultPresenter.SafeCode(outcome.AdminDatabaseState)}");
        output.WriteLine($"Admin 到達性: {DescribeAdminExposure(outcome.AdminExposure)}");
        output.WriteLine($"login 確認: {SetupAssistantResultPresenter.SafeCode(outcome.LoginVerification)}");
        output.WriteLine(
            $"状態画面の確認: {SetupAssistantResultPresenter.SafeCode(outcome.SetupStatusVerification)}");
        output.WriteLine(
            $"検証 session の後始末: {SetupAssistantResultPresenter.SafeCode(outcome.VerificationSessionCleanup)}");
        output.WriteLine($"手動対応: {(outcome.ManualActionRequired ? "必要" : "不要")}");
        output.WriteLine("Admin bootstrap の結果は Main setup とは独立しています。");
    }

    internal static void WriteDockerPreflight(TextWriter output, SetupAssistantDockerPreflightOutcome preflight)
    {
        WriteOutcomeHeader(
            output,
            preflight.Passed ? SetupAssistantOutcomeKind.Succeeded : SetupAssistantOutcomeKind.Failed);
        output.WriteLine(SetupAssistantResultPresenter.Describe(preflight.Code));
        WriteCodeRow(output, "結果コード", preflight.Code);
        output.WriteLine(
            $"Docker engine: {SetupAssistantResultPresenter.SafeLabel(preflight.EngineKind, "未判定")}");
    }

    internal static void WriteAdminPreflight(TextWriter output, SetupAssistantAdminPreflightOutcome preflight)
    {
        WriteOutcomeHeader(
            output,
            preflight.Satisfied ? SetupAssistantOutcomeKind.Succeeded : SetupAssistantOutcomeKind.Rejected);
        output.WriteLine(preflight.Satisfied
            ? "Admin access profile の事前条件を満たしています。host と Admin データベースの状態に依存する残りの判定は bootstrap 実行時に行われます。"
            : "Admin access profile の条件を満たしていません。Admin は無効のまま維持します。");
        output.WriteLine($"access profile: {preflight.Profile}");
        WriteCodeRow(output, "理由コード", preflight.ReasonCode);
    }

    internal static void WriteRejection(TextWriter output, string rejectionKey) =>
        output.WriteLine(SetupAssistantResultPresenter.DescribeRejection(rejectionKey));

    internal static void WriteFinalSummary(TextWriter output, SetupTerminalRunSummary summary)
    {
        output.WriteLine();
        output.WriteLine("=== Easy Setup 終了 ===");
        output.WriteLine($"mainSetup.status: {summary.MainSetupStatusWire}");
        output.WriteLine($"adminBootstrap.status: {summary.AdminBootstrapStatusWire}");
        output.WriteLine();
        output.WriteLine($"Main setup: {(summary.MainSetupSucceeded ? "成功" : "未完了")}");
        output.WriteLine($"Staging verification: {DescribeStagingState(summary)}");
        output.WriteLine($"Deployment send-ready: {(summary.DeploymentSendReady ? "到達" : "未到達")}");
        output.WriteLine($"Admin bootstrap: {DescribeAdminBootstrapState(summary)}");
        output.WriteLine("実送信による運用確認: 記録していません。");
        if (summary.MainSetupSucceeded && summary.AdminBootstrapFailedOrCancelled)
        {
            output.WriteLine("Main setup は成功しています。Admin bootstrap の結果は Main setup に影響しません。");
        }

        output.WriteLine();
        output.WriteLine("次に確認する場所:");
        output.WriteLine("- Admin を有効化した場合は、Admin の setup status 画面で現在の構成を確認できます。");
        output.WriteLine("- Admin を有効化していない場合や mode 5 を利用する場合は、配布物に含まれる setup runbook を参照してください。");
    }

    internal static void WriteManualModeGuidance(TextWriter output)
    {
        output.WriteLine("mode 5（production ACS + Queue）は Easy Setup の自動化対象外です。この Assistant は設定を変更しません。");
        output.WriteLine();
        output.WriteLine("手順:");
        output.WriteLine("- 配布物に含まれる setup runbook と bounce ingestion runbook に従って手動で構成してください。");
        output.WriteLine("- Queue と bounce の設定は Manual Deployment の契約のまま維持されます。");
    }

    private static void WriteOutcomeHeader(TextWriter output, SetupAssistantOutcomeKind kind) =>
        output.WriteLine(KindLabel(kind));

    private static void WriteCodeRow(TextWriter output, string label, string? code) =>
        output.WriteLine($"{label}: {SetupAssistantResultPresenter.SafeCode(code)}");

    private static string KindLabel(SetupAssistantOutcomeKind kind) => kind switch
    {
        SetupAssistantOutcomeKind.Succeeded => "[成功]",
        SetupAssistantOutcomeKind.Rejected => "[入力却下]",
        SetupAssistantOutcomeKind.Failed => "[FAIL]",
        SetupAssistantOutcomeKind.ActionRequired => "[ACTION]",
        SetupAssistantOutcomeKind.ManualInterventionRequired => "[手動対応が必要]",
        _ => "[結果]",
    };

    private static string DescribeAdminExposure(string exposure) =>
        SetupAssistantResultPresenter.SafeCode(exposure) switch
        {
            "disabled" => "無効（disabled）",
            "enabled" => "有効（enabled）",
            _ => "不明（unknown）。Admin の状態画面または runbook で現在の構成を確認してください。",
        };

    private static string DescribeStagingState(SetupTerminalRunSummary summary) =>
        summary.Staging is null
            ? "未実施"
            : summary.Staging.Kind == SetupAssistantOutcomeKind.Succeeded ? "送信要求受理" : "失敗";

    private static string DescribeAdminBootstrapState(SetupTerminalRunSummary summary) =>
        summary.AdminBootstrapStatusWire switch
        {
            "not_requested" => "未実施",
            "declined" => "実行していません（skip）",
            "succeeded" => "成功",
            "failed" => "失敗",
            "cancelled" => "中止",
            _ => "-",
        };
}

internal enum SetupTerminalMainSetupStatus
{
    NotStarted = 0,
    Succeeded = 1,
    Failed = 2,
    CancelledClean = 3,
    Cancelled = 4,
}

internal enum SetupTerminalAdminBootstrapStatus
{
    NotRequested = 0,
    Declined = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
}

internal sealed class SetupTerminalRunSummary
{
    internal SetupTerminalMainSetupStatus MainSetupStatus { get; init; }

    internal SetupTerminalAdminBootstrapStatus AdminBootstrapStatus { get; init; }

    internal bool MainSetupSucceeded { get; init; }

    internal bool DeploymentSendReady { get; init; }

    internal SetupAssistantStagingOutcome? Staging { get; init; }

    internal bool AdminBootstrapFailedOrCancelled =>
        AdminBootstrapStatus is SetupTerminalAdminBootstrapStatus.Failed
            or SetupTerminalAdminBootstrapStatus.Cancelled;

    internal string MainSetupStatusWire => MainSetupStatus switch
    {
        SetupTerminalMainSetupStatus.Succeeded => "succeeded",
        SetupTerminalMainSetupStatus.Failed => "failed",
        SetupTerminalMainSetupStatus.CancelledClean => "cancelled_clean",
        SetupTerminalMainSetupStatus.Cancelled => "cancelled",
        _ => "failed",
    };

    internal string AdminBootstrapStatusWire => AdminBootstrapStatus switch
    {
        SetupTerminalAdminBootstrapStatus.NotRequested => "not_requested",
        SetupTerminalAdminBootstrapStatus.Declined => "declined",
        SetupTerminalAdminBootstrapStatus.Succeeded => "succeeded",
        SetupTerminalAdminBootstrapStatus.Failed => "failed",
        SetupTerminalAdminBootstrapStatus.Cancelled => "cancelled",
        _ => "not_requested",
    };
}
