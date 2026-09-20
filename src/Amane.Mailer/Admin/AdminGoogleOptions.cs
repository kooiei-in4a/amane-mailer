namespace Amane.Mailer.Admin;

/// <summary>
/// Google Admin login is enabled only when both Client ID and Client Secret are
/// non-empty. Incomplete configuration must not fail Mailer startup.
/// Client Secret is never stored on this type so it cannot leak through ToString.
/// </summary>
public sealed class AdminGoogleOptions
{
    public const string ClientIdKey = "AMANE_ADMIN_GOOGLE_CLIENT_ID";
    public const string ClientSecretKey = "AMANE_ADMIN_GOOGLE_CLIENT_SECRET";

    public bool Enabled { get; }

    public string ClientId { get; }

    private AdminGoogleOptions(bool enabled, string clientId)
    {
        Enabled = enabled;
        ClientId = clientId;
    }

    public static AdminGoogleOptions Load(IConfiguration configuration)
    {
        var clientId = ReadTrimmed(configuration, ClientIdKey);
        var clientSecret = ReadTrimmed(configuration, ClientSecretKey);
        var enabled = clientId.Length > 0 && clientSecret.Length > 0;
        return new AdminGoogleOptions(enabled, enabled ? clientId : string.Empty);
    }

    public static string ReadClientSecret(IConfiguration configuration) =>
        ReadTrimmed(configuration, ClientSecretKey);

    private static string ReadTrimmed(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    public override string ToString() =>
        Enabled
            ? "AdminGoogleOptions { Enabled = True }"
            : "AdminGoogleOptions { Enabled = False }";
}
