namespace Amane.Mailer.Setup;

/// <summary>
/// Easy Setup modes 1-4 (ADR 0021 D-11). Mode 5 (production-queue) is rejected by Core.
/// Aligns with SetupDoctorMode names without including ProductionQueue.
/// </summary>
public enum SetupMode
{
    LocalMailpit = 1,
    StagingNoSend = 2,
    StagingVerification = 3,
    ProductionAcs = 4,
}

public static class SetupModeParser
{
    public const string UsageHint =
        "local-mailpit | staging-no-send | staging-verification | production-acs";

    public static bool TryParse(string? raw, out SetupMode mode)
    {
        mode = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var normalized = raw.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "local-mailpit":
                mode = SetupMode.LocalMailpit;
                return true;
            case "staging-no-send":
                mode = SetupMode.StagingNoSend;
                return true;
            case "staging-verification":
                mode = SetupMode.StagingVerification;
                return true;
            case "production-acs":
                mode = SetupMode.ProductionAcs;
                return true;
            default:
                return false;
        }
    }

    public static string ToWireValue(SetupMode mode) => mode switch
    {
        SetupMode.LocalMailpit => "local-mailpit",
        SetupMode.StagingNoSend => "staging-no-send",
        SetupMode.StagingVerification => "staging-verification",
        SetupMode.ProductionAcs => "production-acs",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
