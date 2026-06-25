using System.Text.Json;
using Amane.Mailer.Configuration;
using Amane.Mailer.Json;

namespace Amane.Mailer.Tests;

public sealed class MailerTenantValidationTests
{
    public static IEnumerable<object[]> ExampleTenantFiles()
    {
        yield return ["tenants.example.json"];
        yield return ["tenants.shared.example.json"];
        yield return ["tenants.local-acs.json.example"];
    }

    [Theory]
    [MemberData(nameof(ExampleTenantFiles))]
    public void Example_tenant_json_passes_runtime_validation(string fileName)
    {
        var path = Path.Combine(FindRepositoryRoot(), "config", "mailer", fileName);
        var tenantFile = JsonSerializer.Deserialize(
            File.ReadAllText(path),
            MailerJsonContext.Default.MailerTenantsFile);

        Assert.NotNull(tenantFile);
        tenantFile.Validate();
        foreach (var tenant in tenantFile.Tenants)
        {
            tenant.Validate();
        }
    }

    [Fact]
    public void Tenant_file_rejects_unknown_environment()
    {
        var tenantFile = new MailerTenantsFile
        {
            Version = 1,
            Environment = "prod",
            Tenants = [ValidTenant()],
        };

        var exception = Assert.Throws<InvalidOperationException>(tenantFile.Validate);

        Assert.Contains("environment", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tenant_file_rejects_unknown_version()
    {
        var tenantFile = new MailerTenantsFile
        {
            Version = 2,
            Environment = "develop",
            Tenants = [ValidTenant()],
        };

        var exception = Assert.Throws<InvalidOperationException>(tenantFile.Validate);

        Assert.Contains("version", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tenant_file_rejects_empty_tenants()
    {
        var tenantFile = new MailerTenantsFile
        {
            Version = 1,
            Environment = "develop",
            Tenants = [],
        };

        var exception = Assert.Throws<InvalidOperationException>(tenantFile.Validate);

        Assert.Contains("at least one tenant", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("ExampleService")]
    [InlineData("example.service")]
    [InlineData("-example-service")]
    public void Tenant_rejects_source_services_that_do_not_match_schema_pattern(
        string sourceService)
    {
        var tenant = ValidTenant() with
        {
            SourceServices = [sourceService],
        };

        var exception = Assert.Throws<InvalidOperationException>(tenant.Validate);

        Assert.Contains("source_service", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tenant_rejects_duplicate_source_services()
    {
        var tenant = ValidTenant() with
        {
            SourceServices = ["example-service", "example-service"],
        };

        var exception = Assert.Throws<InvalidOperationException>(tenant.Validate);

        Assert.Contains("unique", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tenant_rejects_token_env_that_does_not_match_schema_pattern()
    {
        var tenant = ValidTenant() with
        {
            TokenEnv = "mail_service_token",
        };

        var exception = Assert.Throws<InvalidOperationException>(tenant.Validate);

        Assert.Contains("token_env", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tenant_rejects_empty_default_from_display_name()
    {
        var tenant = ValidTenant() with
        {
            DefaultFrom = new MailerAddress
            {
                Email = "noreply@example.com",
                DisplayName = string.Empty,
            },
        };

        var exception = Assert.Throws<InvalidOperationException>(tenant.Validate);

        Assert.Contains("display_name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tenant_rejects_retry_max_attempts_above_schema_maximum()
    {
        var tenant = ValidTenant() with
        {
            Retry = ValidRetry() with
            {
                MaxAttempts = MailerRetryOptions.MaxAttemptsUpperBound + 1,
            },
        };

        var exception = Assert.Throws<InvalidOperationException>(tenant.Validate);

        Assert.Contains("max_attempts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tenant_json_rejects_unmapped_properties()
    {
        const string json = """
            {
              "version": 1,
              "environment": "develop",
              "unexpected": true,
              "tenants": []
            }
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(json, MailerJsonContext.Default.MailerTenantsFile));
    }

    private static MailerTenant ValidTenant() => new()
    {
        TenantId = Guid.Parse("00000000-0000-0000-0000-000000000101"),
        Name = "example-develop",
        SourceServices = ["example-service"],
        DefaultFrom = new MailerAddress
        {
            Email = "noreply@example.com",
            DisplayName = "Example Service",
        },
        TokenEnv = "MAIL_SERVICE_TOKEN",
        Provider = "mailpit",
        LiveSending = false,
        MetadataMaxBytes = 4096,
        Retry = ValidRetry(),
    };

    private static MailerRetryOptions ValidRetry() => new()
    {
        MaxAttempts = 10,
        InitialDelaySeconds = 10,
        MaxDelaySeconds = 300,
    };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Amane.Mailer.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(
            directory is not null,
            $"Could not find repository root containing Amane.Mailer.slnx from {AppContext.BaseDirectory}.");
        return directory.FullName;
    }
}
