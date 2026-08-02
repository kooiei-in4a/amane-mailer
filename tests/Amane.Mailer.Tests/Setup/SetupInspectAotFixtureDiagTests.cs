using Amane.Mailer.Configuration;
using Amane.Mailer.Setup;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests.Setup;

public sealed class SetupInspectAotFixtureDiagTests
{
    private const string SmokeTenantsJsonWithMetadataMaxBytes =
        "{\"version\":1,\"environment\":\"develop\",\"tenants\":[{\"tenant_id\":\"00000000-0000-0000-0000-000000000101\",\"name\":\"aot-inspect\",\"source_services\":[\"aot\"],\"default_from\":{\"email\":\"noreply@example.com\",\"display_name\":\"AOT\"},\"token_env\":\"MAIL_SERVICE_TOKEN\",\"provider\":\"mailpit\",\"live_sending\":false,\"metadata_max_bytes\":4096,\"retry\":{\"max_attempts\":3,\"initial_delay_seconds\":1,\"max_delay_seconds\":10}}]}";

    private const string SmokeTenantsJsonWithoutMetadataMaxBytes =
        "{\"version\":1,\"environment\":\"develop\",\"tenants\":[{\"tenant_id\":\"00000000-0000-0000-0000-000000000101\",\"name\":\"aot-inspect\",\"source_services\":[\"aot\"],\"default_from\":{\"email\":\"noreply@example.com\",\"display_name\":\"AOT\"},\"token_env\":\"MAIL_SERVICE_TOKEN\",\"provider\":\"mailpit\",\"live_sending\":false,\"retry\":{\"max_attempts\":3,\"initial_delay_seconds\":1,\"max_delay_seconds\":10}}]}";

    private const string SmokeTenantsJsonWithExplicitZeroMetadataMaxBytes =
        "{\"version\":1,\"environment\":\"develop\",\"tenants\":[{\"tenant_id\":\"00000000-0000-0000-0000-000000000101\",\"name\":\"aot-inspect\",\"source_services\":[\"aot\"],\"default_from\":{\"email\":\"noreply@example.com\",\"display_name\":\"AOT\"},\"token_env\":\"MAIL_SERVICE_TOKEN\",\"provider\":\"mailpit\",\"live_sending\":false,\"metadata_max_bytes\":0,\"retry\":{\"max_attempts\":3,\"initial_delay_seconds\":1,\"max_delay_seconds\":10}}]}";

    private const string InspectToken = "aot-inspect-token-not-real";

    [Fact]
    public void Aot_smoke_tenants_json_matches_native_path_smoke_fixture_and_tryload_succeeds()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "tenants.json");
        File.WriteAllText(path, SmokeTenantsJsonWithMetadataMaxBytes);

        var config = BuildConfig(
            ("MAILER_TENANTS_PATH", path),
            ("MAIL_SERVICE_TOKEN", InspectToken));

        var load = MailerConfigurationSnapshot.TryLoad(config);
        Assert.True(load.Succeeded, load.FailureKind.ToString());
        Assert.NotNull(load.Snapshot);
    }

    [Fact]
    public void Omitted_metadata_max_bytes_defaults_to_4096_and_tryload_succeeds()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "tenants.json");
        File.WriteAllText(path, SmokeTenantsJsonWithoutMetadataMaxBytes);

        var config = BuildConfig(
            ("MAILER_TENANTS_PATH", path),
            ("MAIL_SERVICE_TOKEN", InspectToken));

        var load = MailerConfigurationSnapshot.TryLoad(config);
        Assert.True(load.Succeeded, load.FailureKind.ToString());
        Assert.NotNull(load.Snapshot);
        Assert.Equal(
            MailerTenant.DefaultMetadataMaxBytes,
            load.Snapshot.TenantsFile.Tenants[0].EffectiveMetadataMaxBytes);
        Assert.Equal(
            MailerTenant.DefaultMetadataMaxBytes,
            load.Snapshot.TenantsFile.Tenants[0].MetadataMaxBytes);
    }

    [Fact]
    public void Explicit_zero_metadata_max_bytes_yields_tenants_invalid()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "tenants.json");
        File.WriteAllText(path, SmokeTenantsJsonWithExplicitZeroMetadataMaxBytes);

        var config = BuildConfig(
            ("MAILER_TENANTS_PATH", path),
            ("MAIL_SERVICE_TOKEN", InspectToken));

        var load = MailerConfigurationSnapshot.TryLoad(config);
        Assert.False(load.Succeeded);
        Assert.Equal(
            MailerConfigurationSnapshot.LoadFailureKind.TenantsInvalid,
            load.FailureKind);
    }

    [Fact]
    public void Aot_smoke_fixture_inspect_effective_is_not_managed_mailpit_success()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "tenants.json");
        File.WriteAllText(path, SmokeTenantsJsonWithMetadataMaxBytes);

        var config = BuildConfig(
            ("MAILER_TENANTS_PATH", path),
            ("MAIL_SERVICE_TOKEN", InspectToken));

        var result = SetupInspectEffectiveEngine.Inspect(config);
        Assert.False(result.Managed);
        Assert.Equal("mailpit", result.Effective.ProviderSummary);
        Assert.Equal(SetupInspectIntegrityResult.NotManaged, result.BundleIntegrity.Result);
        Assert.Equal(SetupInspectEffectiveCommand.SuccessExitCode, SetupInspectEffectiveCommand.ResolveExitCode(result));
    }

    private static IConfiguration BuildConfig(params (string Key, string Value)[] pairs)
    {
        var data = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            data[key] = value;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(data)
            .Build();
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "amane-inspect-aot-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
