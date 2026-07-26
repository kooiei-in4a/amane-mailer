using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Json;

namespace Amane.Mailer.Api;

/// <summary>
/// Bearer token, tenant, and source_service authorization for mail-request endpoints.
/// Auth failure paths stay explicit so 401/403 conditions remain readable at call sites.
/// </summary>
public static class TenantRequestAuthorizer
{
    public static string? ReadBearerToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";

        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorization[prefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    public static bool TryAuthorizeCreate(
        MailerTenantRegistry tenantRegistry,
        Guid tenantId,
        string sourceService,
        string? bearerToken,
        out MailerTenant? tenant,
        out IResult? error)
    {
        tenant = tenantRegistry.Authorize(tenantId, bearerToken);
        if (tenant is null)
        {
            error = MailRequestHttpErrorMapper.Error(
                StatusCodes.Status401Unauthorized,
                MailerErrorCodes.UnauthorizedTenant);
            return false;
        }

        if (!tenant.IsSourceServiceAllowed(sourceService))
        {
            error = MailRequestHttpErrorMapper.Error(
                StatusCodes.Status403Forbidden,
                MailerErrorCodes.SourceServiceNotAllowed);
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryAuthorizeScoped(
        HttpRequest httpRequest,
        MailerTenantRegistry tenantRegistry,
        string mailRequestId,
        out Guid tenantId,
        out string sourceService,
        out Guid parsedMailRequestId,
        out IResult? error)
    {
        tenantId = default;
        sourceService = string.Empty;
        parsedMailRequestId = default;
        error = null;

        if (!Guid.TryParse(mailRequestId, out parsedMailRequestId))
        {
            error = MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                "mail_request_id must be a UUID.",
                StatusCodes.Status400BadRequest);
            return false;
        }

        if (!TryParseRequiredGuidQuery(httpRequest, "tenant_id", out tenantId, out var queryParseError))
        {
            error = MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                queryParseError,
                StatusCodes.Status400BadRequest);
            return false;
        }

        if (!TryParseRequiredSourceServiceQuery(httpRequest, out sourceService, out queryParseError))
        {
            error = MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                queryParseError,
                StatusCodes.Status400BadRequest);
            return false;
        }

        var bearerToken = ReadBearerToken(httpRequest);
        var tenant = tenantRegistry.Authorize(tenantId, bearerToken);
        if (tenant is null)
        {
            error = MailRequestHttpErrorMapper.Error(
                StatusCodes.Status401Unauthorized,
                MailerErrorCodes.UnauthorizedTenant);
            return false;
        }

        if (!tenant.IsSourceServiceAllowed(sourceService))
        {
            error = MailRequestHttpErrorMapper.Error(
                StatusCodes.Status403Forbidden,
                MailerErrorCodes.SourceServiceNotAllowed);
            return false;
        }

        return true;
    }

    private static bool TryParseRequiredGuidQuery(
        HttpRequest request,
        string name,
        out Guid value,
        out string error)
    {
        if (!request.Query.TryGetValue(name, out var rawValues) || rawValues.Count == 0)
        {
            value = default;
            error = $"{name} is required.";
            return false;
        }

        if (!Guid.TryParse(rawValues[0], out value))
        {
            error = $"{name} must be a UUID.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryParseRequiredSourceServiceQuery(
        HttpRequest request,
        out string value,
        out string error)
    {
        if (!request.Query.TryGetValue("source_service", out var rawValues) || rawValues.Count == 0)
        {
            value = string.Empty;
            error = "source_service is required.";
            return false;
        }

        value = rawValues[0] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "source_service must not be empty.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
