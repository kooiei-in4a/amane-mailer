using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Amane.Mailer.Tests;

/// <summary>
/// Keeps startup validation inventory aligned with AddStartupValidatedSingleton registrations (#351).
/// </summary>
public sealed class MailerStartupValidationInventoryTests
{
    // Keep in sync with AddStartupValidatedSingleton calls in
    // AmaneMailerServiceCollectionExtensions and AdminServiceRegistration.
    // When adding a startup-required options type, register it with
    // AddStartupValidatedSingleton and extend this list.
    public static readonly Type[] ExpectedStartupValidatedTypes =
    [
        typeof(MailerAdminOptions),
        typeof(MailerAdminDbOpsOptions),
        typeof(MailerTenantRegistry),
        typeof(MailerOptions),
        typeof(MailerWorkerOptions),
        typeof(MailerSweepOptions),
        typeof(MailerRetentionOptions),
        typeof(MailerAdminAuditRetentionOptions),
        typeof(MailerWebhookOptions),
        typeof(MailerHealthcheckOptions),
        typeof(MailerMetricsOptions),
    ];

    [Fact]
    public void Catalog_matches_expected_startup_validated_types()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "false",
                ["ConnectionStrings:Mailer"] = "Data Source=:memory:",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new InventoryHostEnvironment());
        services.AddLogging();
        services.AddAmaneMailerServices(configuration);

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<MailerStartupValidationCatalog>();

        Assert.Equal(
            SortTypes(ExpectedStartupValidatedTypes),
            SortTypes(catalog.ServiceTypes.ToArray()));
    }

    [Fact]
    public void Program_cs_does_not_enumerate_startup_options_types()
    {
        var programPath = Path.Combine(FindRepositoryRoot(), "src", "Amane.Mailer", "Program.cs");
        var source = File.ReadAllText(programPath);

        Assert.Contains("MailerStartupValidator", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetRequiredService<MailerTenantRegistry>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetRequiredService<MailerOptions>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetRequiredService<MailerMetricsOptions>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetRequiredService<MailerWorkerOptions>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetRequiredService<MailerWebhookOptions>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetRequiredService<MailerSweepOptions>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetRequiredService<MailerRetentionOptions>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetRequiredService<MailerAdminAuditRetentionOptions>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetRequiredService<MailerHealthcheckOptions>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Service_registration_uses_startup_validated_singleton_for_options_factories()
    {
        // Guards against registering a Load-validated options type with plain AddSingleton
        // while forgetting AddStartupValidatedSingleton (catalog drift).
        var registrationPaths = new[]
        {
            Path.Combine(FindRepositoryRoot(), "src", "Amane.Mailer", "AmaneMailerServiceCollectionExtensions.cs"),
            Path.Combine(FindRepositoryRoot(), "src", "Amane.Mailer", "Admin", "AdminServiceRegistration.cs"),
        };

        foreach (var path in registrationPaths)
        {
            var source = File.ReadAllText(path);
            Assert.Contains("AddStartupValidatedSingleton", source, StringComparison.Ordinal);
            Assert.DoesNotContain("services.AddSingleton(provider =>", source, StringComparison.Ordinal);
        }
    }

    private static string[] SortTypes(IEnumerable<Type> types) =>
        types.Select(static type => type.FullName!).OrderBy(static name => name, StringComparer.Ordinal).ToArray();

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Amane.Mailer.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Could not locate repository root from test base directory.");
    }

    private sealed class InventoryHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Amane.Mailer.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
