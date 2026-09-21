using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Admin;

/// <summary>
/// Saved vs process-startup Google Login status for /admin/auth-settings.
/// Google Login only — not a generic configuration framework.
/// </summary>
internal sealed class AdminGoogleSettingsStatus
{
    public const string DisplayOn = "ON";
    public const string DisplayOnIncomplete = "ON（設定不完全）";
    public const string DisplayOff = "OFF";
    public const string ReflectionApplied = "反映済み";
    public const string ReflectionRestartPending = "再起動待ち";
    public const string ReflectionIncomplete = "設定不完全";
    public const string AuthorityManaged = "Admin UI managed configuration";
    public const string AuthorityLegacy = "legacy env (until first save)";

    private AdminGoogleSettingsStatus(
        bool savedRequestedEnabled,
        bool savedComplete,
        bool savedEffectiveEnabled,
        bool savedUsesManaged,
        bool runtimeEnabled,
        bool runtimeUsesManaged,
        bool restartRequired,
        string? savedClientId)
    {
        SavedRequestedEnabled = savedRequestedEnabled;
        SavedComplete = savedComplete;
        SavedEffectiveEnabled = savedEffectiveEnabled;
        SavedUsesManaged = savedUsesManaged;
        RuntimeEnabled = runtimeEnabled;
        RuntimeUsesManaged = runtimeUsesManaged;
        RestartRequired = restartRequired;
        SavedClientId = savedClientId ?? string.Empty;
    }

    public bool SavedRequestedEnabled { get; }

    public bool SavedComplete { get; }

    public bool SavedEffectiveEnabled { get; }

    public bool SavedUsesManaged { get; }

    public bool RuntimeEnabled { get; }

    public bool RuntimeUsesManaged { get; }

    public bool RestartRequired { get; }

    public string SavedClientId { get; }

    public string SavedEnabledDisplay =>
        !SavedRequestedEnabled
            ? DisplayOff
            : SavedComplete
                ? DisplayOn
                : DisplayOnIncomplete;

    public string RuntimeEnabledDisplay =>
        RuntimeEnabled ? DisplayOn : DisplayOff;

    public string ReflectionDisplay =>
        SavedRequestedEnabled && !SavedComplete
            ? ReflectionIncomplete
            : RestartRequired
                ? ReflectionRestartPending
                : ReflectionApplied;

    public string SavedAuthorityDisplay =>
        SavedUsesManaged ? AuthorityManaged : AuthorityLegacy;

    public string RuntimeAuthorityDisplay =>
        RuntimeUsesManaged ? AuthorityManaged : AuthorityLegacy;

    public static AdminGoogleSettingsStatus Evaluate(
        InstanceConfigurationRow? instanceConfiguration,
        AdminGoogleOptions runtimeOptions,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(runtimeOptions);
        ArgumentNullException.ThrowIfNull(configuration);

        var savedUsesManaged = instanceConfiguration is not null
            && AdminGoogleOptions.UsesManagedAuthority(
                ToRuntimeState(instanceConfiguration));

        if (!savedUsesManaged)
        {
            // No managed claim yet: durable "saved" Google settings are the legacy env
            // already loaded into this process. Nothing is restart-pending.
            return new AdminGoogleSettingsStatus(
                savedRequestedEnabled: runtimeOptions.Enabled,
                savedComplete: true,
                savedEffectiveEnabled: runtimeOptions.Enabled,
                savedUsesManaged: false,
                runtimeEnabled: runtimeOptions.Enabled,
                runtimeUsesManaged: runtimeOptions.UsesManagedConfiguration,
                restartRequired: false,
                savedClientId: runtimeOptions.ClientId);
        }

        var savedState = ToRuntimeState(instanceConfiguration!);
        var savedOptions = AdminGoogleOptions.Load(configuration, savedState);
        var savedRequested = instanceConfiguration!.GoogleLoginEnabled;
        var savedComplete = !savedRequested || savedOptions.Enabled;
        var savedEffective = savedOptions.Enabled;
        var restartRequired = ComputeRestartRequired(
            runtimeOptions,
            savedOptions,
            savedState,
            configuration);

        return new AdminGoogleSettingsStatus(
            savedRequested,
            savedComplete,
            savedEffective,
            savedUsesManaged: true,
            runtimeOptions.Enabled,
            runtimeOptions.UsesManagedConfiguration,
            restartRequired,
            savedOptions.Enabled ? savedOptions.ClientId : instanceConfiguration.GoogleClientId);
    }

    private static bool ComputeRestartRequired(
        AdminGoogleOptions runtime,
        AdminGoogleOptions saved,
        InstanceRuntimeState savedState,
        IConfiguration configuration)
    {
        if (runtime.UsesManagedConfiguration != saved.UsesManagedConfiguration)
            return true;

        if (runtime.Enabled != saved.Enabled)
            return true;

        if (!runtime.Enabled && !saved.Enabled)
            return false;

        // Both effective-enabled: Client ID or Client Secret drift requires restart.
        if (!string.Equals(runtime.ClientId, saved.ClientId, StringComparison.Ordinal))
            return true;

        var currentSecret = AdminGoogleOptions.ReadClientSecret(configuration, savedState);
        return !runtime.MatchesCurrentSecret(currentSecret);
    }

    private static InstanceRuntimeState ToRuntimeState(InstanceConfigurationRow row) =>
        new(
            row.InitializedAt is null
                ? InstanceRuntimeStateKind.Uninitialized
                : InstanceRuntimeStateKind.Initialized,
            row.InitializedAt,
            row.LiveSending,
            row.ProviderType,
            row.ProviderSecretRef,
            row.ProviderConfiguredAt,
            HasInstanceOwner: true,
            row.GoogleLoginEnabled,
            row.GoogleClientId,
            row.GoogleClientSecretRef,
            row.GoogleConfiguredAt);
}
