namespace Amane.Mailer.Setup;

public sealed class SetupResult
{
    public required string Code { get; init; }
    public string? BundleId { get; init; }
    public string? ConfigurationFingerprint { get; init; }
    public SetupPlan? Plan { get; init; }
    public string? Message { get; init; }

    public bool IsSuccess =>
        Code is SetupResultCode.Succeeded or SetupResultCode.DryRunPlan;

    public static SetupResult Ok(string code, string bundleId, string fingerprint, SetupPlan? plan, string? message = null) =>
        new()
        {
            Code = code,
            BundleId = bundleId,
            ConfigurationFingerprint = fingerprint,
            Plan = plan,
            Message = message,
        };

    public static SetupResult Fail(string code, string message) =>
        new()
        {
            Code = code,
            Message = message,
        };
}
