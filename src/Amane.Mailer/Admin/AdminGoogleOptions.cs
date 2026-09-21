using Amane.Mailer.Configuration;

namespace Amane.Mailer.Admin;

/// <summary>
/// Google Admin login is enabled only when the effective toggle is on and both
/// Client ID and Client Secret are non-empty. Incomplete configuration must not
/// fail Mailer startup. Client Secret is never stored on this type so it cannot
/// leak through ToString.
/// <para>
/// Authority: when the instance is initialized and managed Google settings have
/// been saved at least once (<c>google_configured_at</c>), Admin UI managed
/// configuration is exclusive authority and env Google keys are ignored.
/// Otherwise the legacy env path remains the compatibility authority.
/// </para>
/// </summary>
public sealed class AdminGoogleOptions
{
    public const string ClientIdKey = "AMANE_ADMIN_GOOGLE_CLIENT_ID";
    public const string ClientSecretKey = "AMANE_ADMIN_GOOGLE_CLIENT_SECRET";

    public bool Enabled { get; }

    public string ClientId { get; }

    /// <summary>
    /// True when this process is using Admin UI managed Google settings (not env).
    /// </summary>
    public bool UsesManagedConfiguration { get; }

    private AdminGoogleOptions(bool enabled, string clientId, bool usesManagedConfiguration)
    {
        Enabled = enabled;
        ClientId = clientId;
        UsesManagedConfiguration = usesManagedConfiguration;
    }

    public static AdminGoogleOptions Load(IConfiguration configuration) =>
        Load(configuration, instanceState: null);

    public static AdminGoogleOptions Load(
        IConfiguration configuration,
        InstanceRuntimeState? instanceState)
    {
        if (UsesManagedAuthority(instanceState))
        {
            var clientId = instanceState!.GoogleClientId?.Trim() ?? string.Empty;
            var secretConfigured = AdminGoogleSecretStore.IsSecretConfigured(
                instanceState.GoogleClientSecretRef);
            var enabled = instanceState.GoogleLoginEnabled
                && clientId.Length > 0
                && secretConfigured;
            return new AdminGoogleOptions(
                enabled,
                enabled ? clientId : string.Empty,
                usesManagedConfiguration: true);
        }

        var envClientId = ReadTrimmed(configuration, ClientIdKey);
        var envClientSecret = ReadTrimmed(configuration, ClientSecretKey);
        var envEnabled = envClientId.Length > 0 && envClientSecret.Length > 0;
        return new AdminGoogleOptions(
            envEnabled,
            envEnabled ? envClientId : string.Empty,
            usesManagedConfiguration: false);
    }

    public static string ReadClientSecret(
        IConfiguration configuration,
        InstanceRuntimeState? instanceState = null)
    {
        if (UsesManagedAuthority(instanceState))
        {
            var path = instanceState!.GoogleClientSecretRef;
            return AdminGoogleSecretStore.TryReadSecret(path ?? string.Empty, out var secret)
                ? secret
                : string.Empty;
        }

        return ReadTrimmed(configuration, ClientSecretKey);
    }

    public static bool UsesManagedAuthority(InstanceRuntimeState? instanceState) =>
        instanceState?.IsInitialized == true
        && !string.IsNullOrWhiteSpace(instanceState.GoogleConfiguredAt);

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
