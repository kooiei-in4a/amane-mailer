using System.Reflection;
using System.Text.RegularExpressions;
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
        typeof(MailerBounceIngestionOptions),
    ];

    // Matches AddSingleton(x => ...) / AddSingleton<T>(x => ...) factory forms regardless of
    // parameter name, then looks ahead for a .Load( call in the same factory body.
    private static readonly Regex PlainSingletonLoadFactoryPattern = new(
        @"\.AddSingleton(?:\s*<[^>]+>)?\s*\(\s*[A-Za-z_][A-Za-z0-9_]*\s*=>[\s\S]{0,1200}?\.Load\s*\(",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    [Fact]
    public void Catalog_matches_expected_startup_validated_types()
    {
        var catalog = BuildCatalog();

        Assert.Equal(
            SortTypes(ExpectedStartupValidatedTypes),
            SortTypes(catalog.ServiceTypes.ToArray()));
    }

    [Fact]
    public void Catalog_includes_every_assembly_Load_entry_point_with_IConfiguration()
    {
        // Structural guard: a new *Options / registry type with Load(IConfiguration...) that is
        // registered via plain AddSingleton never enters ExpectedStartupValidatedTypes either.
        // Comparing assembly Load entry points to the live catalog catches that omission class.
        var loadEntryPoints = typeof(MailerOptions).Assembly
            .GetTypes()
            .Where(static type => type is { IsClass: true, IsAbstract: false })
            .Where(static type => type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Any(static method =>
                    method.Name == "Load"
                    && method.GetParameters()
                        .Any(static parameter => parameter.ParameterType == typeof(IConfiguration))))
            .ToArray();

        var catalog = BuildCatalog();
        var missing = loadEntryPoints
            .Where(type => !catalog.ServiceTypes.Contains(type))
            .Select(static type => type.FullName)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Startup catalog is missing Load(IConfiguration...) entry points: "
            + string.Join(", ", missing)
            + ". Register with AddStartupValidatedSingleton and extend ExpectedStartupValidatedTypes.");
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
    public void Service_registration_does_not_use_plain_AddSingleton_Load_factories()
    {
        var root = Path.Combine(FindRepositoryRoot(), "src", "Amane.Mailer");
        var registrationPaths = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(static path =>
            {
                var name = Path.GetFileName(path);
                return name.Contains("ServiceCollection", StringComparison.Ordinal)
                    || name.Contains("ServiceRegistration", StringComparison.Ordinal)
                    || string.Equals(name, "Program.cs", StringComparison.Ordinal);
            })
            .ToArray();

        Assert.NotEmpty(registrationPaths);

        foreach (var path in registrationPaths)
        {
            var source = File.ReadAllText(path);
            Assert.False(
                PlainSingletonLoadFactoryPattern.IsMatch(source),
                $"{path} still registers a Load factory with plain AddSingleton; use AddStartupValidatedSingleton.");

            if (path.EndsWith("AmaneMailerServiceCollectionExtensions.cs", StringComparison.Ordinal)
                || path.EndsWith("AdminServiceRegistration.cs", StringComparison.Ordinal))
            {
                Assert.Contains("AddStartupValidatedSingleton", source, StringComparison.Ordinal);
            }
        }
    }

    private static MailerStartupValidationCatalog BuildCatalog()
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
        return provider.GetRequiredService<MailerStartupValidationCatalog>();
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
