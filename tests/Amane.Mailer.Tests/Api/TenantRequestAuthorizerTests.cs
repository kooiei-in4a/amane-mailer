using Amane.Mailer.Api;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests.Api;

public sealed class TenantRequestAuthorizerTests : IAsyncLifetime
{
    private string? _root;
    private MailerTenantRegistry? _registry;

    public async ValueTask InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "amane-mailer-auth-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var tenantsPath = Path.Combine(_root, "tenants.json");
        await File.WriteAllTextAsync(tenantsPath, TenantConfigJson);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:TenantsPath"] = tenantsPath,
                ["MAIL_SERVICE_TOKEN"] = MailerWebApplicationFixtureBase.Token,
            })
            .Build();

        _registry = MailerTenantRegistry.Load(configuration, "Testing");
    }

    public ValueTask DisposeAsync()
    {
        if (_root is not null && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public void TryAuthorizeCreate_rejects_missing_token_with_401()
    {
        var authorized = TenantRequestAuthorizer.TryAuthorizeCreate(
            _registry!,
            MailerWebApplicationFixtureBase.TenantId,
            MailerWebApplicationFixtureBase.SourceService,
            bearerToken: null,
            out _,
            out var error);

        Assert.False(authorized);
        var (statusCode, body) = MailRequestHttpResultAssertions.Inspect(error!);
        Assert.Equal(StatusCodes.Status401Unauthorized, statusCode);
        Assert.Contains(MailerErrorCodes.UnauthorizedTenant, body, StringComparison.Ordinal);
    }

    [Fact]
    public void TryAuthorizeCreate_rejects_disallowed_source_service_with_403()
    {
        var authorized = TenantRequestAuthorizer.TryAuthorizeCreate(
            _registry!,
            MailerWebApplicationFixtureBase.TenantId,
            "other-service",
            MailerWebApplicationFixtureBase.Token,
            out _,
            out var error);

        Assert.False(authorized);
        var (statusCode, body) = MailRequestHttpResultAssertions.Inspect(error!);
        Assert.Equal(StatusCodes.Status403Forbidden, statusCode);
        Assert.Contains(MailerErrorCodes.SourceServiceNotAllowed, body, StringComparison.Ordinal);
    }

    [Fact]
    public void TryAuthorizeCreate_accepts_valid_tenant_and_source_service()
    {
        var authorized = TenantRequestAuthorizer.TryAuthorizeCreate(
            _registry!,
            MailerWebApplicationFixtureBase.TenantId,
            MailerWebApplicationFixtureBase.SourceService,
            MailerWebApplicationFixtureBase.Token,
            out var tenant,
            out var error);

        Assert.True(authorized);
        Assert.Null(error);
        Assert.NotNull(tenant);
        Assert.Equal(MailerWebApplicationFixtureBase.TenantId, tenant.TenantId);
    }

    [Fact]
    public void TryAuthorizeScoped_rejects_unauthorized_token_with_401()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer wrong-token";
        context.Request.QueryString = new QueryString(
            $"?tenant_id={MailerWebApplicationFixtureBase.TenantId}&source_service={MailerWebApplicationFixtureBase.SourceService}");

        var authorized = TenantRequestAuthorizer.TryAuthorizeScoped(
            context.Request,
            _registry!,
            Guid.NewGuid().ToString(),
            out _,
            out _,
            out _,
            out var error);

        Assert.False(authorized);
        var (statusCode, body) = MailRequestHttpResultAssertions.Inspect(error!);
        Assert.Equal(StatusCodes.Status401Unauthorized, statusCode);
        Assert.Contains(MailerErrorCodes.UnauthorizedTenant, body, StringComparison.Ordinal);
    }

    [Fact]
    public void TryAuthorizeScoped_rejects_disallowed_source_service_with_403()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bearer {MailerWebApplicationFixtureBase.Token}";
        context.Request.QueryString = new QueryString(
            $"?tenant_id={MailerWebApplicationFixtureBase.TenantId}&source_service=other-service");

        var authorized = TenantRequestAuthorizer.TryAuthorizeScoped(
            context.Request,
            _registry!,
            Guid.NewGuid().ToString(),
            out _,
            out _,
            out _,
            out var error);

        Assert.False(authorized);
        var (statusCode, body) = MailRequestHttpResultAssertions.Inspect(error!);
        Assert.Equal(StatusCodes.Status403Forbidden, statusCode);
        Assert.Contains(MailerErrorCodes.SourceServiceNotAllowed, body, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadBearerToken_reads_authorization_header()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bearer {MailerWebApplicationFixtureBase.Token}";

        Assert.Equal(
            MailerWebApplicationFixtureBase.Token,
            TenantRequestAuthorizer.ReadBearerToken(context.Request));
    }

    private static string TenantConfigJson =>
        $$"""
        {
          "version": 1,
          "environment": "develop",
          "tenants": [
            {
              "tenant_id": "{{MailerWebApplicationFixtureBase.TenantId}}",
              "name": "example-develop",
              "source_services": ["{{MailerWebApplicationFixtureBase.SourceService}}"],
              "default_from": {
                "email": "noreply@example.com",
                "display_name": "Example Service"
              },
              "token_env": "MAIL_SERVICE_TOKEN",
              "provider": "mailpit",
              "live_sending": false,
              "metadata_max_bytes": 4096,
              "retry": {
                "max_attempts": 3,
                "initial_delay_seconds": 1,
                "max_delay_seconds": 2
              }
            }
          ]
        }
        """;
}
