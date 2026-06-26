using System.Net;

namespace Amane.Mailer.Admin;

public sealed record MailerAdminOptions
{
    private const string AllowedLocalAddressKey = "AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS";
    private const string AllowedLocalAddressFallbackKey = "MAILER_ADMIN_ALLOWED_LOCAL_ADDRESS";
    private const string DeprecatedBindAddressKey = "AMANE_ADMIN_BIND";
    private const string DeprecatedBindAddressFallbackKey = "MAILER_ADMIN_BIND";

    public bool Enabled { get; init; }

    public string Username { get; init; } = "admin";

    public string PasswordHash { get; init; } = string.Empty;

    public string AllowedLocalAddress { get; init; } = "127.0.0.1";

    public int LoginFailureLimit { get; init; } = 5;

    public TimeSpan LoginCooldown { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan SessionIdleTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan SessionAbsoluteLifetime { get; init; } = TimeSpan.FromHours(12);

    public bool MaskRecipients { get; init; } = true;

    public bool MaskSubjects { get; init; } = true;

    public static MailerAdminOptions Load(IConfiguration configuration)
    {
        var enabled = ReadBoolean(configuration, "AMANE_ADMIN_ENABLED", "MAILER_ADMIN_ENABLED") ?? false;
        var listPiiVisible = string.Equals(
            ReadString(configuration, "AMANE_ADMIN_PII_LIST_MODE", "MAILER_ADMIN_PII_LIST_MODE", string.Empty),
            "visible",
            StringComparison.OrdinalIgnoreCase);
        var defaultMaskPii = !listPiiVisible;

        return new()
        {
            Enabled = enabled,
            Username = ReadString(configuration, "AMANE_ADMIN_USERNAME", "MAILER_ADMIN_USERNAME", "admin"),
            PasswordHash = ReadString(configuration, "AMANE_ADMIN_PASSWORD_HASH", "MAILER_ADMIN_PASSWORD_HASH", string.Empty),
            AllowedLocalAddress = ReadString(
                configuration,
                "127.0.0.1",
                AllowedLocalAddressKey,
                AllowedLocalAddressFallbackKey,
                DeprecatedBindAddressKey,
                DeprecatedBindAddressFallbackKey),
            MaskRecipients = ReadBoolean(configuration, "AMANE_ADMIN_MASK_RECIPIENTS", "MAILER_ADMIN_MASK_RECIPIENTS")
                ?? defaultMaskPii,
            MaskSubjects = ReadBoolean(configuration, "AMANE_ADMIN_MASK_SUBJECTS", "MAILER_ADMIN_MASK_SUBJECTS")
                ?? defaultMaskPii,
        };
    }

    public void Validate()
    {
        if (!Enabled)
            return;

        if (string.IsNullOrWhiteSpace(Username))
            throw new InvalidOperationException("AMANE_ADMIN_USERNAME must not be empty when AMANE_ADMIN_ENABLED=true.");

        if (string.IsNullOrWhiteSpace(PasswordHash))
            throw new InvalidOperationException("AMANE_ADMIN_PASSWORD_HASH must be set when AMANE_ADMIN_ENABLED=true.");

        if (!AdminPasswordHasher.IsSupportedHash(PasswordHash))
            throw new InvalidOperationException("AMANE_ADMIN_PASSWORD_HASH must use the pbkdf2:sha256 format.");

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

    private static bool? ReadBoolean(IConfiguration configuration, string primaryKey, string fallbackKey)
    {
        var value = configuration[primaryKey];
        if (!string.IsNullOrWhiteSpace(value))
            return bool.TryParse(value, out var parsed) && parsed;

        value = configuration[fallbackKey];
        if (!string.IsNullOrWhiteSpace(value))
            return bool.TryParse(value, out var parsed) && parsed;

        return null;
    }
}
