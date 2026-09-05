using System.Net;
using Amane.Mailer.Configuration;

namespace Amane.Mailer.Admin;

public sealed record MailerAdminOptions
{
    private const string AllowedLocalAddressKey = "AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS";
    private const string AllowedLocalAddressFallbackKey = "MAILER_ADMIN_ALLOWED_LOCAL_ADDRESS";
    private const string DeprecatedBindAddressKey = "AMANE_ADMIN_BIND";
    private const string DeprecatedBindAddressFallbackKey = "MAILER_ADMIN_BIND";

    public bool Enabled { get; init; }

    /// <summary>
    /// True when an initialized managed v2 instance owns the Admin credential state in SQLite.
    /// In this mode the legacy environment password hash is intentionally ignored on restart,
    /// and <c>AMANE_ADMIN_ENABLED</c> is not the Admin surface gate.
    /// </summary>
    public bool DatabaseOwnedCredentials { get; init; }

    public string Username { get; init; } = "admin";

    public string PasswordHash { get; init; } = string.Empty;

    public string AllowedLocalAddress { get; init; } = "127.0.0.1";

    public int LoginFailureLimit { get; init; } = 5;

    public TimeSpan LoginCooldown { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan SessionIdleTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan SessionAbsoluteLifetime { get; init; } = TimeSpan.FromHours(12);

    public int MaxConcurrentSessions { get; init; } = 3;

    public bool HashNetworkIdentifiers { get; init; }

    public byte[]? AuditIdentifierHashKey { get; init; }

    public bool MaskRecipients { get; init; } = true;

    public bool MaskSubjects { get; init; } = true;

    /// <summary>
    /// True only when <c>MAILER_ADMIN_PII_LIST_MODE=visible</c> (or AMANE_ alias).
    /// Suppressions list unmask requires this explicit opt-in (ADR 0013 D-05);
    /// <see cref="MaskRecipients"/>=false alone must not unmask that page.
    /// </summary>
    public bool ListPiiVisible { get; init; }

    public static MailerAdminOptions Load(IConfiguration configuration) =>
        Load(configuration, databaseOwnedCredentials: false);

    public static MailerAdminOptions Load(
        IConfiguration configuration,
        bool databaseOwnedCredentials)
    {
        // ENABLED is always strict-parsed. Other Admin UI settings are only load-bearing when
        // Admin is on (mirrors Validate early-return), so typos must not abort mail delivery.
        var enabled = ConfigurationBooleanReader.Read(
            configuration,
            defaultValue: false,
            "AMANE_ADMIN_ENABLED",
            "MAILER_ADMIN_ENABLED");
        if (!enabled && !databaseOwnedCredentials)
            return new() { Enabled = false };

        var listPiiVisible = string.Equals(
            ReadString(configuration, "AMANE_ADMIN_PII_LIST_MODE", "MAILER_ADMIN_PII_LIST_MODE", string.Empty),
            "visible",
            StringComparison.OrdinalIgnoreCase);
        var defaultMaskPii = !listPiiVisible;
        var hashNetworkIdentifiers = ConfigurationBooleanReader.Read(
            configuration,
            defaultValue: false,
            "AMANE_ADMIN_AUDIT_HASH_NETWORK_IDENTIFIERS",
            "MAILER_ADMIN_AUDIT_HASH_NETWORK_IDENTIFIERS");

        return new()
        {
            Enabled = true,
            DatabaseOwnedCredentials = databaseOwnedCredentials,
            Username = ReadString(configuration, "AMANE_ADMIN_USERNAME", "MAILER_ADMIN_USERNAME", "admin"),
            // Do not even retain the legacy environment hash in the DB-owned runtime options.
            PasswordHash = databaseOwnedCredentials
                ? string.Empty
                : ReadString(configuration, "AMANE_ADMIN_PASSWORD_HASH", "MAILER_ADMIN_PASSWORD_HASH", string.Empty),
            AllowedLocalAddress = ReadString(
                configuration,
                "127.0.0.1",
                AllowedLocalAddressKey,
                AllowedLocalAddressFallbackKey,
                DeprecatedBindAddressKey,
                DeprecatedBindAddressFallbackKey),
            LoginFailureLimit = ReadPositiveInt(
                configuration,
                5,
                "AMANE_ADMIN_LOGIN_FAILURE_LIMIT",
                "MAILER_ADMIN_LOGIN_FAILURE_LIMIT"),
            LoginCooldown = ReadPositiveSeconds(
                configuration,
                30,
                "AMANE_ADMIN_LOGIN_COOLDOWN_SECONDS",
                "MAILER_ADMIN_LOGIN_COOLDOWN_SECONDS"),
            SessionIdleTimeout = ReadPositiveMinutes(
                configuration,
                30,
                "AMANE_ADMIN_SESSION_IDLE_MINUTES",
                "MAILER_ADMIN_SESSION_IDLE_MINUTES"),
            SessionAbsoluteLifetime = ReadPositiveHours(
                configuration,
                12,
                "AMANE_ADMIN_SESSION_ABSOLUTE_HOURS",
                "MAILER_ADMIN_SESSION_ABSOLUTE_HOURS"),
            MaxConcurrentSessions = ReadPositiveInt(
                configuration,
                3,
                "AMANE_ADMIN_MAX_CONCURRENT_SESSIONS",
                "MAILER_ADMIN_MAX_CONCURRENT_SESSIONS"),
            HashNetworkIdentifiers = hashNetworkIdentifiers,
            AuditIdentifierHashKey = hashNetworkIdentifiers
                ? AdminNetworkIdentifierHasher.TryParseKey(ReadString(
                    configuration,
                    "AMANE_ADMIN_AUDIT_IDENTIFIER_HASH_KEY",
                    "MAILER_ADMIN_AUDIT_IDENTIFIER_HASH_KEY",
                    string.Empty))
                : null,
            ListPiiVisible = listPiiVisible,
            MaskRecipients = ConfigurationBooleanReader.ReadOptional(
                    configuration,
                    "AMANE_ADMIN_MASK_RECIPIENTS",
                    "MAILER_ADMIN_MASK_RECIPIENTS")
                ?? defaultMaskPii,
            MaskSubjects = ConfigurationBooleanReader.ReadOptional(
                    configuration,
                    "AMANE_ADMIN_MASK_SUBJECTS",
                    "MAILER_ADMIN_MASK_SUBJECTS")
                ?? defaultMaskPii,
        };
    }

    public void Validate() => Validate(databaseOwnedCredentials: DatabaseOwnedCredentials);

    public void Validate(bool databaseOwnedCredentials)
    {
        if (!Enabled)
            return;

        if (string.IsNullOrWhiteSpace(Username))
            throw new InvalidOperationException("AMANE_ADMIN_USERNAME must not be empty when AMANE_ADMIN_ENABLED=true.");

        if (!databaseOwnedCredentials && string.IsNullOrWhiteSpace(PasswordHash))
            throw new InvalidOperationException("AMANE_ADMIN_PASSWORD_HASH must be set when AMANE_ADMIN_ENABLED=true.");

        if (!databaseOwnedCredentials && !AdminPasswordHasher.IsSupportedHash(PasswordHash))
        {
            throw new InvalidOperationException(
                "AMANE_ADMIN_PASSWORD_HASH must use pbkdf2:sha256 with iterations "
                + $"{AdminPasswordHasher.MinIterations}-{AdminPasswordHasher.MaxIterations}, "
                + $"salt {AdminPasswordHasher.MinSaltSize}-{AdminPasswordHasher.MaxSaltSize} bytes, "
                + $"and hash {AdminPasswordHasher.MinHashSize}-{AdminPasswordHasher.MaxHashSize} bytes. "
                + "Generate a compliant hash with 'admin hash-password'. Legacy weaker hashes are rejected.");
        }

        if (!IPAddress.TryParse(AllowedLocalAddress, out _))
            throw new InvalidOperationException(
                "AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS must be an IP address. " +
                "MAILER_ADMIN_ALLOWED_LOCAL_ADDRESS and deprecated AMANE_ADMIN_BIND / MAILER_ADMIN_BIND aliases " +
                "use the same value.");

        if (LoginFailureLimit <= 0)
            throw new InvalidOperationException("AMANE_ADMIN_LOGIN_FAILURE_LIMIT must be greater than zero.");

        if (LoginCooldown <= TimeSpan.Zero)
            throw new InvalidOperationException("AMANE_ADMIN_LOGIN_COOLDOWN_SECONDS must be greater than zero.");

        if (SessionIdleTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("AMANE_ADMIN_SESSION_IDLE_MINUTES must be greater than zero.");

        if (SessionAbsoluteLifetime <= SessionIdleTimeout)
            throw new InvalidOperationException("AMANE_ADMIN_SESSION_ABSOLUTE_HOURS must be greater than the idle timeout.");

        if (MaxConcurrentSessions <= 0)
            throw new InvalidOperationException("AMANE_ADMIN_MAX_CONCURRENT_SESSIONS must be greater than zero.");

        if (HashNetworkIdentifiers && AuditIdentifierHashKey is null)
        {
            throw new InvalidOperationException(
                "AMANE_ADMIN_AUDIT_IDENTIFIER_HASH_KEY must be set to a base64-encoded key of at least 32 bytes "
                + "when AMANE_ADMIN_AUDIT_HASH_NETWORK_IDENTIFIERS=true.");
        }
    }

    public string? ResolveAuditSourceIp(string? remoteAddress)
    {
        if (string.IsNullOrWhiteSpace(remoteAddress))
            return null;

        if (!HashNetworkIdentifiers || AuditIdentifierHashKey is null)
            return remoteAddress;

        return new AdminNetworkIdentifierHasher(AuditIdentifierHashKey).HashIdentifier(remoteAddress);
    }

    private static string ReadString(
        IConfiguration configuration,
        string primaryKey,
        string fallbackKey,
        string defaultValue)
    {
        var value = configuration[primaryKey];
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        value = configuration[fallbackKey];
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private static string ReadString(
        IConfiguration configuration,
        string defaultValue,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return defaultValue;
    }

    private static int ReadPositiveInt(
        IConfiguration configuration,
        int defaultValue,
        string primaryKey,
        string fallbackKey) =>
        ReadPositiveDurationValue(configuration, defaultValue, primaryKey, fallbackKey);

    private static TimeSpan ReadPositiveSeconds(
        IConfiguration configuration,
        int defaultSeconds,
        string primaryKey,
        string fallbackKey) =>
        TimeSpan.FromSeconds(ReadPositiveDurationValue(configuration, defaultSeconds, primaryKey, fallbackKey));

    private static TimeSpan ReadPositiveMinutes(
        IConfiguration configuration,
        int defaultMinutes,
        string primaryKey,
        string fallbackKey) =>
        TimeSpan.FromMinutes(ReadPositiveDurationValue(configuration, defaultMinutes, primaryKey, fallbackKey));

    private static TimeSpan ReadPositiveHours(
        IConfiguration configuration,
        int defaultHours,
        string primaryKey,
        string fallbackKey) =>
        TimeSpan.FromHours(ReadPositiveDurationValue(configuration, defaultHours, primaryKey, fallbackKey));

    private static int ReadPositiveDurationValue(
        IConfiguration configuration,
        int defaultValue,
        string primaryKey,
        string fallbackKey)
    {
        var value = configuration[primaryKey];
        var key = primaryKey;
        if (string.IsNullOrWhiteSpace(value))
        {
            value = configuration[fallbackKey];
            key = fallbackKey;
        }

        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new InvalidOperationException(
                $"{key} must be a positive integer.");
        }

        return parsed;
    }
}
