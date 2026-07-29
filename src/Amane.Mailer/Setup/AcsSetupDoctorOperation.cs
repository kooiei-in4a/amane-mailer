namespace Amane.Mailer.Setup;

/// <summary>
/// Console-independent post-apply ACS doctor gate. #450 remains authoritative for effective
/// fingerprint, integrity, readiness, runtime identity, and current verification. This operation
/// evaluates the effective provider/live-sending observation returned by #450 without a send.
/// </summary>
public sealed class AcsSetupDoctorOperation
{
    public AcsSetupDoctorResult EvaluateProduction(SetupApplyResult applyResult)
    {
        if (applyResult.Code != SetupApplyResultCode.ApplySucceeded
            || !applyResult.ConfigurationApplied
            || !applyResult.VerificationCommitted)
        {
            return AcsSetupDoctorResult.Fail("doctor_apply_not_verified");
        }

        if (!string.Equals(
                applyResult.EffectiveProviderSummary,
                "acs",
                StringComparison.Ordinal))
        {
            return AcsSetupDoctorResult.Fail("doctor_effective_provider_not_acs");
        }

        if (applyResult.EffectiveLiveSendingEnabled is not true)
        {
            return AcsSetupDoctorResult.Fail("doctor_effective_live_sending_disabled");
        }

        return AcsSetupDoctorResult.Pass();
    }
}

public sealed class AcsSetupDoctorResult
{
    private AcsSetupDoctorResult(bool passed, string? reasonCode)
    {
        Passed = passed;
        ReasonCode = reasonCode;
    }

    public bool Passed { get; }
    public string? ReasonCode { get; }

    public static AcsSetupDoctorResult Pass() => new(true, null);
    public static AcsSetupDoctorResult Fail(string reasonCode) => new(false, reasonCode);
}
