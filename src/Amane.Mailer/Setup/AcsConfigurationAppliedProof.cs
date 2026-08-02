using System.Text.Json.Serialization;

namespace Amane.Mailer.Setup;

/// <summary>
/// Opaque, session-memory proof that the ACS workflow completed its
/// <c>live_sending=false</c> apply stage. Callers can inspect non-secret identity facts but
/// cannot construct a proof or access the captured Setup request.
/// </summary>
public sealed class AcsConfigurationAppliedProof
{
    internal AcsConfigurationAppliedProof(
        string bundleId,
        string configurationFingerprint,
        long activationGeneration,
        SetupRequest appliedRequest)
    {
        BundleId = bundleId;
        ConfigurationFingerprint = configurationFingerprint;
        ActivationGeneration = activationGeneration;
        Mode = appliedRequest.Mode;
        AppliedRequest = appliedRequest;
    }

    public string BundleId { get; }
    public string ConfigurationFingerprint { get; }
    public long ActivationGeneration { get; }
    public SetupMode Mode { get; }

    [JsonIgnore]
    internal SetupRequest AppliedRequest { get; }

    internal SetupExpectedActiveAuthority ToExpectedAuthority() =>
        new()
        {
            BundleId = BundleId,
            ConfigurationFingerprint = ConfigurationFingerprint,
            ActivationGeneration = ActivationGeneration,
        };
}
