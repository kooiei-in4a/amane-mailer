namespace Amane.Mailer.Operations;

public enum SetupDoctorMode
{
    LocalMailpit,
    StagingNoSend,
    StagingVerification,
    ProductionAcs,
    ProductionQueue,
}

public static class SetupDoctorModeParser
{
    public const string UsageHint =
        "local-mailpit | staging-no-send | staging-verification | production-acs | production-queue";

    public static bool TryParse(string? raw, out SetupDoctorMode mode)
    {
        mode = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        mode = raw.Trim().ToLowerInvariant() switch
        {
            "local-mailpit" => SetupDoctorMode.LocalMailpit,
            "staging-no-send" => SetupDoctorMode.StagingNoSend,
            "staging-verification" => SetupDoctorMode.StagingVerification,
            "production-acs" => SetupDoctorMode.ProductionAcs,
            "production-queue" => SetupDoctorMode.ProductionQueue,
            _ => default,
        };

        return raw.Trim().ToLowerInvariant() switch
        {
            "local-mailpit" or "staging-no-send" or "staging-verification" or "production-acs" or "production-queue" => true,
            _ => false,
        };
    }
}
