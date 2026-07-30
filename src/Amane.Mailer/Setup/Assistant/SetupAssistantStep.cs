namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// The two independent transactions the assistant drives. Admin bootstrap can only start after
/// the main setup transaction has succeeded, and its failure never invalidates that success
/// (ADR 0021 D-08).
/// </summary>
internal enum SetupAssistantTransaction
{
    MainSetup = 0,
    AdminBootstrap = 1,
}

/// <summary>
/// Screens of the assistant, in the order defined by issue #452. Steps 1-10 belong to the main
/// setup transaction; steps 11-14 belong to the optional Admin bootstrap transaction.
/// </summary>
internal enum SetupAssistantStep
{
    Welcome = 1,
    DockerPreflight = 2,
    ModeSelection = 3,
    TenantBasics = 4,
    ProviderSettings = 5,
    AcsSettings = 6,
    ApplyConfirmation = 7,
    ApplyOutcome = 8,
    DeploymentVerification = 9,
    MainSetupComplete = 10,
    AdminChoice = 11,
    AdminAccessPreflight = 12,
    AdminBootstrapOutcome = 13,
    FinalGuidance = 14,
    Cancelled = 90,
    ManualModeGuidance = 91,
}

internal static class SetupAssistantStepInfo
{
    internal static SetupAssistantTransaction TransactionOf(SetupAssistantStep step) =>
        step >= SetupAssistantStep.AdminChoice && step <= SetupAssistantStep.FinalGuidance
            ? SetupAssistantTransaction.AdminBootstrap
            : SetupAssistantTransaction.MainSetup;

    /// <summary>Ordinal shown to the operator so the current stage is always explicit.</summary>
    internal static int DisplayNumber(SetupAssistantStep step) => (int)step;

    internal const int TotalSteps = 14;

    internal static string Title(SetupAssistantStep step) => step switch
    {
        SetupAssistantStep.Welcome => "ようこそ・対応範囲",
        SetupAssistantStep.DockerPreflight => "Docker preflight",
        SetupAssistantStep.ModeSelection => "Setup mode 選択",
        SetupAssistantStep.TenantBasics => "Tenant 基本設定",
        SetupAssistantStep.ProviderSettings => "Provider 設定",
        SetupAssistantStep.AcsSettings => "ACS 設定",
        SetupAssistantStep.ApplyConfirmation => "適用前確認",
        SetupAssistantStep.ApplyOutcome => "適用・実コンテナ照合",
        SetupAssistantStep.DeploymentVerification => "Staging verification / Production 有効化",
        SetupAssistantStep.MainSetupComplete => "Main setup 完了",
        SetupAssistantStep.AdminChoice => "Admin を有効化するか選択",
        SetupAssistantStep.AdminAccessPreflight => "Admin access profile preflight",
        SetupAssistantStep.AdminBootstrapOutcome => "Admin bootstrap・login 確認",
        SetupAssistantStep.FinalGuidance => "最終案内",
        SetupAssistantStep.Cancelled => "中止",
        SetupAssistantStep.ManualModeGuidance => "Manual runbook 案内",
        _ => "Easy Setup",
    };
}
