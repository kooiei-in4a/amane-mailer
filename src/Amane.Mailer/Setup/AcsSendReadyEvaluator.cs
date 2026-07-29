namespace Amane.Mailer.Setup;

public static class AcsSendReadyEvaluator
{
    public const string SendReadyReady = "ready";
    public const string SendReadyNotReady = "not-ready";
    public const string ReasonLiveSendingDisabled = "live_sending_disabled";
    public const string ReasonApplyNotSucceeded = "apply_not_succeeded";
    public const string ReasonWrongMode = "mode_not_production_acs";
    public const string ReasonDoctorChecksFailed = "doctor_checks_failed";

    public sealed class Evaluation
    {
        public required bool SendReadyAsserted { get; init; }
        public required string SendReadyEvaluation { get; init; }
        public required string? ReasonCode { get; init; }
    }

    public static Evaluation Evaluate(
        SetupMode mode,
        SetupApplyResult applyResult,
        bool effectiveLiveSendingEnabled,
        AcsSetupDoctorResult doctorResult)
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

        if (!doctorResult.Passed)
        {
            return NotReady(doctorResult.ReasonCode ?? ReasonDoctorChecksFailed);
        }

        return new Evaluation
        {
            SendReadyAsserted = true,
            SendReadyEvaluation = SendReadyReady,
            ReasonCode = null,
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
