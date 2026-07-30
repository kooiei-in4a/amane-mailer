namespace Amane.Mailer.Setup;

/// <summary>
/// Lock-scoped proof of the current committed ACTIVE. Only the apply engine may construct it.
/// </summary>
internal sealed class TrustedVerifiedActiveBundle
{
    internal TrustedVerifiedActiveBundle(
        SetupActivePointer active,
        SetupRecordedMetadata recorded,
        SetupVerificationRecord verification,
        SetupRuntimeIdentityBindingStamp runtimeIdentityBinding,
        SourceAdminDisposition adminDisposition)
    {
        Active = active;
        Recorded = recorded;
        Verification = verification;
        RuntimeIdentityBinding = runtimeIdentityBinding;
        AdminDisposition = adminDisposition;
    }

    internal SetupActivePointer Active { get; }
    internal SetupRecordedMetadata Recorded { get; }
    internal SetupVerificationRecord Verification { get; }
    internal SetupRuntimeIdentityBindingStamp RuntimeIdentityBinding { get; }
    internal SourceAdminDisposition AdminDisposition { get; }

    internal SetupExpectedActiveAuthority ToExpectedActiveAuthority() =>
        new()
        {
            BundleId = Active.BundleId,
            ActivationGeneration = Active.ActivationGeneration,
            ConfigurationFingerprint = Recorded.ConfigurationFingerprint,
        };

    internal AdminBootstrapSourceAuthority ToDurableSourceAuthority(DateTimeOffset capturedAt) =>
        new()
        {
            BundleId = Active.BundleId,
            ActivationGeneration = Active.ActivationGeneration,
            ConfigurationFingerprint = Recorded.ConfigurationFingerprint,
            RecordedSchemaVersion = Recorded.SchemaVersion,
            ImageIdentity = (Recorded.ImageRepository ?? string.Empty)
                + ":"
                + (Recorded.ImageTag ?? string.Empty),
            ComposeIdentity = Verification.ComposeIdentity ?? string.Empty,
            RuntimeIdentityBindingDigest = RuntimeIdentityBinding.BindingMac,
            AdminDisposition = AdminDisposition,
            CapturedAt = capturedAt.UtcDateTime.ToString("O"),
        };
}
