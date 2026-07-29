namespace Amane.Mailer.Setup;

/// <summary>
/// Fail-closed gate that allows Setup Core to materialize a Production bundle with
/// tenant <c>live_sending=true</c>. Only the ACS typed workflow may set this after exact
/// Production confirmation and explicit live-sending approval. Adapters must not forge it.
/// </summary>
public sealed class SetupLiveSendingPromotionAuthorization
{
    /// <summary>True only after ordinal exact <c>Production</c> confirmation succeeded.</summary>
    public required bool ProductionEnvironmentConfirmed { get; init; }

    /// <summary>True only after the operator typed the live-sending enable approval phrase.</summary>
    public required bool LiveSendingEnableApproved { get; init; }

    public bool IsAuthorized =>
        ProductionEnvironmentConfirmed && LiveSendingEnableApproved;
}
