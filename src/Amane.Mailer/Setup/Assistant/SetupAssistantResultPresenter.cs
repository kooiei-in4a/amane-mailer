using System.Text.RegularExpressions;
using Amane.Mailer.Operations.AdminBootstrap;

namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// Converts canonical result codes from the typed operations into fixed operator-facing text.
/// Any code without an entry falls back to a generic sentence, so a provider message, exception
/// text, raw Docker output, or host path can never reach the screen.
/// </summary>
internal static partial class SetupAssistantResultPresenter
{
    internal const string UnknownResultText =
        "処理は完了しませんでした。詳細は表示できません。手順を確認して再実行してください。";

    internal static string Describe(string? code) => code switch
    {
        null or "" => UnknownResultText,

        SetupDockerResultCode.Succeeded =>
            "Docker preflight に成功しました。",
        SetupDockerResultCode.DockerUnavailable =>
            "Docker CLI を利用できません。Docker を起動してから再実行してください。",
        SetupDockerResultCode.DockerVersionUnsupported =>
            "Docker のバージョンが対応範囲外です。",
        SetupDockerResultCode.ComposeUnavailable =>
            "Docker Compose plugin を利用できません。",
        SetupDockerResultCode.ComposeVersionUnsupported =>
            "Docker Compose のバージョンが対応範囲外です。Compose v2 以上が必要です。",
        SetupDockerResultCode.RemoteDockerRejected or SetupDockerResultCode.RemoteContextRejected =>
            "remote Docker は Easy Setup の対象外です。ローカル Docker で実行してください。",
        SetupDockerResultCode.UnsupportedDockerEnvironment =>
            "この Docker 環境は Easy Setup の対応範囲外です。",
        SetupDockerResultCode.ConcurrentSetupRejected =>
            "別の setup 操作が進行中です。完了を待って再実行してください。",
        SetupDockerResultCode.Timeout =>
            "Docker 操作がタイムアウトしました。再実行できます。",
        SetupDockerResultCode.Cancelled =>
            "Docker 操作が中止されました。",

        SetupResultCode.RejectedValidation =>
            "入力内容が設定契約を満たしていません。前の画面へ戻って修正してください。",
        SetupResultCode.RejectedModeUnsupported =>
            "選択された mode は Easy Setup の対象外です。",
        SetupResultCode.RejectedConflictManual =>
            "既存の Manual 構成と競合しています。Manual 手順の確認が必要です。",
        SetupResultCode.RejectedConcurrentExecution or SetupResultCode.RejectedLockFailed =>
            "別の setup 操作が進行中です。完了を待って再実行してください。",

        AcsSetupResultCode.ConfigurationApplied =>
            "設定 bundle を適用しました。実送信可否はまだ確認していません。",
        AcsSetupResultCode.DeploymentSendReady =>
            "Deployment send-ready に到達しました。実送信による運用確認は行っていません。",
        AcsSetupResultCode.BundleGenerationFailed =>
            "設定 bundle の生成に失敗しました。入力を見直して再実行してください。",
        AcsSetupResultCode.ConfigurationApplyFailed =>
            "設定の適用に失敗しました。",
        AcsSetupResultCode.StagingVerificationSucceeded =>
            "Staging verification の送信要求は受け付けられました。受信箱の確認は手動で行ってください。",
        AcsSetupResultCode.StagingVerificationFailed =>
            "Staging verification に失敗しました。",
        AcsSetupResultCode.ProductionConfirmationRejected =>
            "Production の確認入力が一致しませんでした。正確に入力してください。",
        AcsSetupResultCode.RejectedLiveSendingWithoutConfirmation =>
            "live_sending の有効化には正確な承認フレーズが必要です。",
        AcsSetupResultCode.LiveSendingEnableApplyFailed =>
            "live_sending 有効化の適用に失敗しました。",
        AcsSetupResultCode.ConfigRollbackSucceeded =>
            "適用に失敗したため、直前の設定へ戻しました。",
        AcsSetupResultCode.ConfigRollbackFailed =>
            "適用に失敗し、設定の巻き戻しにも失敗しました。手動の復旧が必要です。",
        AcsSetupResultCode.ExternalSideEffectMayRemain =>
            "外部への副作用が残っている可能性があります。手動の確認が必要です。",
        AcsSetupResultCode.ManualActionRequired =>
            "自動処理を継続できません。手動の対応が必要です。",
        AcsSetupResultCode.RejectedInvalidMode =>
            "この操作は選択中の mode では実行できません。",
        AcsSetupResultCode.FailedUnexpected =>
            UnknownResultText,

        SetupApplyResultCode.ApplySucceeded =>
            "設定 bundle を適用し、実コンテナとの照合に成功しました。",
        SetupApplyResultCode.FreshApplyFailed =>
            "初回適用に失敗しました。巻き戻し先が無いため、成功扱いにはできません。",
        SetupApplyResultCode.ApplyFailedRollbackSucceeded =>
            "適用に失敗したため、直前の設定へ戻しました。",
        SetupApplyResultCode.ApplyFailedRollbackFailed =>
            "適用に失敗し、設定の巻き戻しにも失敗しました。手動の復旧が必要です。",
        SetupApplyResultCode.RecoveryRequired =>
            "中断された適用が残っています。復旧処理が必要です。",
        SetupApplyResultCode.NeedsIntervention =>
            "自動処理を継続できません。手動の対応が必要です。",
        SetupApplyResultCode.ConcurrentApplyRejected =>
            "別の適用処理が進行中です。完了を待って再実行してください。",
        SetupApplyResultCode.UpgradeRequired =>
            "既存の環境は Easy Setup の新規適用対象ではありません。Manual 手順が必要です。",
        SetupApplyResultCode.IneligibleExistingActive =>
            "既存の有効な構成があるため、この適用は実行できません。",
        SetupApplyResultCode.PreflightFailed =>
            "適用前の Docker 確認に失敗しました。",
        SetupApplyResultCode.CancelledBeforeActivation =>
            "有効化の前に処理が中止されました。設定は変更されていません。",

        AdminBootstrapResultCode.Succeeded =>
            "Admin を有効化し、login と状態表示の確認に成功しました。",
        AdminBootstrapResultCode.PreflightRejected =>
            "Admin access profile の条件を満たしていません。Admin は無効のまま維持します。",
        AdminBootstrapResultCode.BundleGenerationFailed =>
            "Admin 有効化用 bundle の生成に失敗しました。Admin は無効のままです。",
        AdminBootstrapResultCode.ApplyFailed =>
            "Admin 有効化の適用に失敗しました。",
        AdminBootstrapResultCode.AccessVerificationFailed =>
            "Admin の login 確認に失敗しました。",
        AdminBootstrapResultCode.ConfigRollbackSucceeded =>
            "Admin 有効化に失敗したため、設定を元に戻しました。",
        AdminBootstrapResultCode.ConfigRollbackFailed =>
            "Admin 有効化に失敗し、設定の巻き戻しにも失敗しました。手動の復旧が必要です。",
        AdminBootstrapResultCode.ManualActionRequired =>
            "Admin の状態を自動で確定できません。手動の対応が必要です。",

        _ => UnknownResultText,
    };

    internal static string DescribeAction(string? actionCode) => actionCode switch
    {
        null or "" => string.Empty,
        SetupApplyActionCode.CompleteSendReadyEvaluation =>
            "send-ready 判定が未完了です。この画面の手順を続けてください。",
        SetupApplyActionCode.ReviewDatabaseSchema =>
            "データベース schema の確認が必要です。運用手順を参照してください。",
        SetupApplyActionCode.ReviewDatabaseFiles =>
            "データベースファイルの状態確認が必要です。運用手順を参照してください。",
        SetupApplyActionCode.ManualInterventionRequired =>
            "手動の介入が必要です。運用手順を参照してください。",
        SetupApplyActionCode.UnsafeVerifierResidue =>
            "検証用の一時ファイルが残っています。手動の確認が必要です。",
        _ => "追加の手動対応が必要です。運用手順を参照してください。",
    };

    /// <summary>
    /// Fixed catalog of input rejections. The assistant never echoes the rejected value back,
    /// so no operator-supplied text can reach the response through this path.
    /// </summary>
    internal static string DescribeRejection(string? key) => key switch
    {
        null or "" => string.Empty,
        SetupAssistantRejection.MissingRequiredField =>
            "必須項目が入力されていません。",
        SetupAssistantRejection.InvalidEmail =>
            "メールアドレスの形式が正しくありません。",
        SetupAssistantRejection.InvalidIdentifier =>
            "名前は英数字とハイフン・アンダースコアのみで入力してください。",
        SetupAssistantRejection.SecretTooShort =>
            "秘密情報が短すぎます。より長い値を設定してください。",
        SetupAssistantRejection.SecretMismatch =>
            "確認入力が一致しません。",
        SetupAssistantRejection.ConfirmationPhraseMismatch =>
            "確認フレーズが完全に一致していません。大文字と小文字を含めて正確に入力してください。",
        SetupAssistantRejection.ModeNotSelectable =>
            "選択できない mode です。",
        SetupAssistantRejection.StepNotAvailable =>
            "この操作は現在の段階では実行できません。",
        SetupAssistantRejection.AdminRequiresMainSetup =>
            "Admin bootstrap は Main setup の成功後にのみ開始できます。",
        SetupAssistantRejection.AdminProfileNotSelectable =>
            "選択できない access profile です。",
        SetupAssistantRejection.StaleRequest =>
            "画面の内容が古くなっています。設定は変更していません。現在の段階からやり直してください。",
        SetupAssistantRejection.InvalidOrigin =>
            "Admin の接続先 URL が正しくありません。",
        _ => "入力を確認してください。",
    };

    /// <summary>
    /// Guards the raw code shown next to the translated sentence. Only a canonical identifier
    /// shape is echoed, so a free-text provider message, an exception body, a host path, or a
    /// connection string can never reach the screen through the code field.
    /// </summary>
    internal static string SafeCode(string? code) =>
        !string.IsNullOrEmpty(code) && code.Length <= 80 && CanonicalCodePattern().IsMatch(code)
            ? code
            : UnrecognizedCode;

    internal const string UnrecognizedCode = "unrecognized";

    /// <summary>Same guard for optional status labels, with a caller-supplied absent value.</summary>
    internal static string SafeLabel(string? label, string absentText) =>
        string.IsNullOrEmpty(label) ? absentText : SafeCode(label);

    [GeneratedRegex(@"^[A-Za-z0-9]+([._-][A-Za-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalCodePattern();

    internal static SetupAssistantOutcomeKind ClassifyApply(
        string code,
        string? actionCode,
        bool persistentSideEffectMayRemain)
    {
        if (persistentSideEffectMayRemain
            || code == AcsSetupResultCode.ExternalSideEffectMayRemain
            || code == AcsSetupResultCode.ManualActionRequired
            || code == AcsSetupResultCode.ConfigRollbackFailed
            || code == SetupApplyResultCode.NeedsIntervention
            || code == SetupApplyResultCode.RecoveryRequired
            || code == SetupApplyResultCode.ApplyFailedRollbackFailed
            || actionCode == SetupApplyActionCode.ManualInterventionRequired
            || actionCode == SetupApplyActionCode.UnsafeVerifierResidue)
        {
            return SetupAssistantOutcomeKind.ManualInterventionRequired;
        }

        if (code is AcsSetupResultCode.ConfigurationApplied
            or AcsSetupResultCode.DeploymentSendReady
            or SetupApplyResultCode.ApplySucceeded)
        {
            return string.IsNullOrEmpty(actionCode)
                ? SetupAssistantOutcomeKind.Succeeded
                : SetupAssistantOutcomeKind.ActionRequired;
        }

        if (code is AcsSetupResultCode.RejectedInvalidMode
            or AcsSetupResultCode.RejectedLiveSendingWithoutConfirmation
            or AcsSetupResultCode.ProductionConfirmationRejected
            or SetupResultCode.RejectedValidation
            or SetupResultCode.RejectedModeUnsupported)
        {
            return SetupAssistantOutcomeKind.Rejected;
        }

        return SetupAssistantOutcomeKind.Failed;
    }
}

internal static class SetupAssistantRejection
{
    internal const string MissingRequiredField = "missing_required_field";
    internal const string InvalidEmail = "invalid_email";
    internal const string InvalidIdentifier = "invalid_identifier";
    internal const string SecretTooShort = "secret_too_short";
    internal const string SecretMismatch = "secret_mismatch";
    internal const string ConfirmationPhraseMismatch = "confirmation_phrase_mismatch";
    internal const string ModeNotSelectable = "mode_not_selectable";
    internal const string StepNotAvailable = "step_not_available";
    internal const string AdminRequiresMainSetup = "admin_requires_main_setup";
    internal const string InvalidOrigin = "invalid_origin";
    internal const string AdminProfileNotSelectable = "admin_profile_not_selectable";
    internal const string StaleRequest = "stale_request";
}
