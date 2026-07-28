namespace Amane.Mailer.Setup;

/// <summary>
/// Canonical integrity / attestation result enums (ADR 0021 D-04).
/// </summary>
public static class SetupInspectIntegrityResult
{
    public const string Matched = "matched";
    public const string Mismatch = "mismatch";
    public const string NotManaged = "not-managed";
    public const string NotVerified = "not-verified";
    public const string InvalidMetadata = "invalid-metadata";
}

/// <summary>
/// Fixed reason codes for inspect-effective. Safe for stdout/stderr; never include secrets.
/// </summary>
public static class SetupInspectReason
{
    public const string MetadataMissing = "metadata-missing";
    public const string MetadataMalformed = "metadata-malformed";
    public const string UnsupportedSchemaVersion = "unsupported-schema-version";
    public const string VerifierMissing = "verifier-missing";
    public const string VerifierMalformed = "verifier-malformed";
    public const string VerifierExpired = "verifier-expired";
    public const string VerifierBundleMismatch = "verifier-bundle-mismatch";
    public const string VerifierMemberSetMismatch = "verifier-member-set-mismatch";
    public const string SecretMissing = "secret-missing";
    public const string MountMismatch = "mount-mismatch";
    public const string HostAtRestPending = "host-at-rest-pending";
    public const string ConfigConflict = "config-conflict";
    public const string TenantsMissing = "tenants-missing";
    public const string TenantsInvalid = "tenants-invalid";
    public const string ProviderInvalid = "provider-invalid";
    public const string MailpitInvalid = "mailpit-invalid";
    public const string FingerprintMismatch = "fingerprint-mismatch";
    public const string CredentialMissing = "credential-missing";
    public const string CredentialInvalid = "credential-invalid";
}

/// <summary>
/// Allowlisted container-side source identifiers. Never echo host private paths.
/// </summary>
public static class SetupInspectSourceIds
{
    public const string ContainerTenants = "container-tenants";
    public const string ContainerAcsFile = "container-acs-file";
    public const string ContainerAcsEnv = "container-acs-env";
    public const string ContainerRecordedMetadata = "container-recorded-metadata";
    public const string NotApplicable = "not-applicable";
}

/// <summary>
/// Safe credential presence states (no secret material).
/// </summary>
public static class SetupInspectCredentialStatus
{
    public const string Loaded = "loaded";
    public const string Missing = "missing";
    public const string Invalid = "invalid";
    public const string NotApplicable = "not-applicable";
}
