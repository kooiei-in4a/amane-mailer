namespace Amane.Mailer.Setup;

/// <summary>
/// Secret-free expected ACTIVE authority for a conditional Managed apply.
/// The apply engine validates these facts under the existing #450 apply lock.
/// </summary>
public sealed class SetupExpectedActiveAuthority
{
    public required string BundleId { get; init; }
    public required string ConfigurationFingerprint { get; init; }
    public required long ActivationGeneration { get; init; }
}
