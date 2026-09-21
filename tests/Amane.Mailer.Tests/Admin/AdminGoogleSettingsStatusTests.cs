using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminGoogleSettingsStatusTests
{
    private const string ClientId = "status-test-client-id.apps.googleusercontent.com";
    private const string ClientIdOther = "status-test-client-id-other.apps.googleusercontent.com";
    private const string Secret = "status-test-client-secret-not-real";
    private const string SecretRotated = "status-test-client-secret-rotated-not-real";

    [Fact]
    public void Unchanged_managed_enabled_is_applied()
    {
        var root = Directory.CreateTempSubdirectory("amane-google-status-");
        try
        {
            var secretPath = Path.Combine(root.FullName, "client_secret");
            AdminGoogleSecretStore.WriteSecret(secretPath, Secret);
            var row = ManagedRow(enabled: true, ClientId, secretPath, configuredAt: "2026-01-01T00:00:00Z");
            var runtime = LoadManaged(row);
            var status = AdminGoogleSettingsStatus.Evaluate(row, runtime, EmptyConfiguration());

            Assert.True(status.SavedEffectiveEnabled);
            Assert.True(status.RuntimeEnabled);
            Assert.False(status.RestartRequired);
            Assert.Equal(AdminGoogleSettingsStatus.ReflectionApplied, status.ReflectionDisplay);
            Assert.Equal(AdminGoogleSettingsStatus.DisplayOn, status.SavedEnabledDisplay);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Enabled_toggle_requires_restart()
    {
        var root = Directory.CreateTempSubdirectory("amane-google-status-");
        try
        {
            var secretPath = Path.Combine(root.FullName, "client_secret");
            AdminGoogleSecretStore.WriteSecret(secretPath, Secret);
            var runtimeRow = ManagedRow(enabled: false, ClientId, secretPath, configuredAt: "2026-01-01T00:00:00Z");
            var runtime = LoadManaged(runtimeRow);
            var saved = ManagedRow(enabled: true, ClientId, secretPath, configuredAt: "2026-01-01T00:00:00Z");
            var status = AdminGoogleSettingsStatus.Evaluate(saved, runtime, EmptyConfiguration());

            Assert.True(status.RestartRequired);
            Assert.Equal(AdminGoogleSettingsStatus.ReflectionRestartPending, status.ReflectionDisplay);
            Assert.Equal(AdminGoogleSettingsStatus.DisplayOn, status.SavedEnabledDisplay);
            Assert.Equal(AdminGoogleSettingsStatus.DisplayOff, status.RuntimeEnabledDisplay);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Client_id_change_requires_restart_when_both_enabled()
    {
        var root = Directory.CreateTempSubdirectory("amane-google-status-");
        try
        {
            var secretPath = Path.Combine(root.FullName, "client_secret");
            AdminGoogleSecretStore.WriteSecret(secretPath, Secret);
            var runtimeRow = ManagedRow(enabled: true, ClientId, secretPath, configuredAt: "2026-01-01T00:00:00Z");
            var runtime = LoadManaged(runtimeRow);
            var saved = ManagedRow(enabled: true, ClientIdOther, secretPath, configuredAt: "2026-01-01T00:00:00Z");
            var status = AdminGoogleSettingsStatus.Evaluate(saved, runtime, EmptyConfiguration());

            Assert.True(status.RestartRequired);
            Assert.Equal(AdminGoogleSettingsStatus.ReflectionRestartPending, status.ReflectionDisplay);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Secret_rotation_same_path_requires_restart()
    {
        var root = Directory.CreateTempSubdirectory("amane-google-status-");
        try
        {
            var secretPath = Path.Combine(root.FullName, "client_secret");
            AdminGoogleSecretStore.WriteSecret(secretPath, Secret);
            var runtimeRow = ManagedRow(enabled: true, ClientId, secretPath, configuredAt: "2026-01-01T00:00:00Z");
            var runtime = LoadManaged(runtimeRow);

            AdminGoogleSecretStore.WriteSecret(secretPath, SecretRotated);
            var saved = ManagedRow(enabled: true, ClientId, secretPath, configuredAt: "2026-01-01T00:00:00Z");
            var status = AdminGoogleSettingsStatus.Evaluate(saved, runtime, EmptyConfiguration());

            Assert.Equal(secretPath, saved.GoogleClientSecretRef);
            Assert.True(status.RestartRequired);
            Assert.Equal(AdminGoogleSettingsStatus.ReflectionRestartPending, status.ReflectionDisplay);
            Assert.False(runtime.MatchesCurrentSecret(SecretRotated));
            Assert.True(runtime.MatchesCurrentSecret(Secret));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Legacy_runtime_with_managed_saved_requires_restart()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AdminGoogleOptions.ClientIdKey] = ClientId,
                [AdminGoogleOptions.ClientSecretKey] = Secret,
            })
            .Build();
        var runtime = AdminGoogleOptions.Load(
            configuration,
            new InstanceRuntimeState(
                InstanceRuntimeStateKind.Initialized,
                "2026-01-01T00:00:00Z",
                false,
                "acs",
                "/tmp/acs",
                "2026-01-01T00:00:00Z",
                true));
        Assert.False(runtime.UsesManagedConfiguration);
        Assert.True(runtime.Enabled);

        var root = Directory.CreateTempSubdirectory("amane-google-status-");
        try
        {
            var secretPath = Path.Combine(root.FullName, "client_secret");
            AdminGoogleSecretStore.WriteSecret(secretPath, Secret);
            var saved = ManagedRow(enabled: true, ClientId, secretPath, configuredAt: "2026-09-21T00:00:00Z");
            var status = AdminGoogleSettingsStatus.Evaluate(saved, runtime, configuration);

            Assert.True(status.RestartRequired);
            Assert.True(status.SavedUsesManaged);
            Assert.False(status.RuntimeUsesManaged);
            Assert.Equal(AdminGoogleSettingsStatus.ReflectionRestartPending, status.ReflectionDisplay);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Incomplete_requested_enabled_prefers_incomplete_display()
    {
        var row = ManagedRow(
            enabled: true,
            ClientId,
            clientSecretRef: null,
            configuredAt: "2026-01-01T00:00:00Z");
        var runtime = LoadManaged(row);
        var status = AdminGoogleSettingsStatus.Evaluate(row, runtime, EmptyConfiguration());

        Assert.True(status.SavedRequestedEnabled);
        Assert.False(status.SavedComplete);
        Assert.False(status.SavedEffectiveEnabled);
        Assert.False(status.RuntimeEnabled);
        Assert.False(status.RestartRequired);
        Assert.Equal(AdminGoogleSettingsStatus.DisplayOnIncomplete, status.SavedEnabledDisplay);
        Assert.Equal(AdminGoogleSettingsStatus.ReflectionIncomplete, status.ReflectionDisplay);
    }

    [Fact]
    public void Unused_client_id_change_while_both_disabled_does_not_require_restart()
    {
        var root = Directory.CreateTempSubdirectory("amane-google-status-");
        try
        {
            var secretPath = Path.Combine(root.FullName, "client_secret");
            AdminGoogleSecretStore.WriteSecret(secretPath, Secret);
            var runtimeRow = ManagedRow(enabled: false, ClientId, secretPath, configuredAt: "2026-01-01T00:00:00Z");
            var runtime = LoadManaged(runtimeRow);
            var saved = ManagedRow(enabled: false, ClientIdOther, secretPath, configuredAt: "2026-01-01T00:00:00Z");
            var status = AdminGoogleSettingsStatus.Evaluate(saved, runtime, EmptyConfiguration());

            Assert.False(status.RestartRequired);
            Assert.Equal(AdminGoogleSettingsStatus.ReflectionApplied, status.ReflectionDisplay);
            Assert.Equal(AdminGoogleSettingsStatus.DisplayOff, status.SavedEnabledDisplay);
            Assert.Equal(AdminGoogleSettingsStatus.DisplayOff, status.RuntimeEnabledDisplay);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static AdminGoogleOptions LoadManaged(InstanceConfigurationRow row)
    {
        var state = new InstanceRuntimeState(
            InstanceRuntimeStateKind.Initialized,
            row.InitializedAt,
            row.LiveSending,
            row.ProviderType,
            row.ProviderSecretRef,
            row.ProviderConfiguredAt,
            true,
            row.GoogleLoginEnabled,
            row.GoogleClientId,
            row.GoogleClientSecretRef,
            row.GoogleConfiguredAt);
        return AdminGoogleOptions.Load(EmptyConfiguration(), state);
    }

    private static InstanceConfigurationRow ManagedRow(
        bool enabled,
        string? clientId,
        string? clientSecretRef,
        string configuredAt) =>
        new(
            "2026-01-01T00:00:00Z",
            false,
            "acs",
            "/tmp/acs",
            "2026-01-01T00:00:00Z",
            enabled,
            clientId,
            clientSecretRef,
            configuredAt);

    private static IConfiguration EmptyConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();
}
