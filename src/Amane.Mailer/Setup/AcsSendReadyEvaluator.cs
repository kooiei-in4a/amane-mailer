namespace Amane.Mailer.Setup;

/// <summary>
/// Evaluates Deployment send-ready for Managed ACS Production after live_sending=true apply.
/// Does not perform or record deployment operational verification or release qualification (#456).
/// </summary>
public static class AcsSendReadyEvaluator
{
    public const string SendReadyReady = "ready";
    public const string SendReadyNotReady = "not-ready";
    public const string ReasonLiveSendingDisabled = "live_sending_disabled";
    public const string ReasonApplyNotSucceeded = "apply_not_succeeded";
    public const string ReasonVerificationIncomplete = "verification_incomplete";
    public const string ReasonWrongMode = "mode_not_production_acs";
    public const string ReasonDoctorPending = "doctor_checks_pending";

    public sealed class Evaluation
    {
        public required bool SendReadyAsserted { get; init; }
        public required string SendReadyEvaluation { get; init; }
        public required string? ReasonCode { get; init; }
    }

    /// <summary>
    /// Send-ready requires Production ACS mode, successful apply with committed verification,
    /// and effective tenant live_sending enabled. Doctor remains a residual ACTION when full
    /// ambient-free doctor is not yet available; Managed apply already covered readiness /
    /// fingerprint / integrity gates (#450).
    /// </summary>
    public static Evaluation Evaluate(
        SetupMode mode,
        SetupApplyResult applyResult,
        bool effectiveLiveSendingEnabled)
    {
        if (mode != SetupMode.ProductionAcs)
        {
            return NotReady(ReasonWrongMode);
        }

        if (applyResult.Code != SetupApplyResultCode.ApplySucceeded
            || !applyResult.ConfigurationApplied
            || !applyResult.VerificationCommitted)
        {
            return NotReady(ReasonApplyNotSucceeded);
        }

        if (!effectiveLiveSendingEnabled)
        {
            return NotReady(ReasonLiveSendingDisabled);
        }

        // Managed apply already required fingerprint, integrity, readiness, and runtime-identity.
        // Remaining doctor ACTION is informational for operators; send-ready is asserted for
        // configuration gates only and must never imply Production operational verification.
        return new Evaluation
        {
            SendReadyAsserted = true,
            SendReadyEvaluation = SendReadyReady,
            ReasonCode = ReasonDoctorPending,
        };
    }

    private static Evaluation NotReady(string reason) =>
        new()
        {
            SendReadyAsserted = false,
            SendReadyEvaluation = SendReadyNotReady,
            ReasonCode = reason,
        };
}
