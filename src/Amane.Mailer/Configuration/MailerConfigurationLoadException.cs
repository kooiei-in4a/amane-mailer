namespace Amane.Mailer.Configuration;

public enum MailerConfigurationLoadFailureKind
{
    None = 0,
    TenantsMissing,
    TenantsInvalid,
    TokenMissing,
    WebhookSecretMissing,
    ProviderInvalid,
    AcsCredentialMissing,
    MailpitInvalid,
}

/// <summary>
/// Typed configuration load failure for shared runtime/inspect classification.
/// Message is display-only; callers must use <see cref="Kind"/>.
/// </summary>
public sealed class MailerConfigurationLoadException : InvalidOperationException
{
    public MailerConfigurationLoadFailureKind Kind { get; }

    public MailerConfigurationLoadException(
        MailerConfigurationLoadFailureKind kind,
        string message)
        : base(message)
    {
        Kind = kind;
    }
}
