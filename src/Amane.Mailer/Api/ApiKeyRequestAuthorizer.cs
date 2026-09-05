using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Identity;
using Amane.Mailer.Json;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Api;

public static class ApiKeyRequestAuthorizer
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

    public static async Task<ApiKeyAuthorizationResult> AuthorizeAsync(
        HttpContext context,
        SenderRepository senders,
        ApiAuthenticationRateLimiter rateLimiter,
        CancellationToken cancellationToken)
    {
        if (!rateLimiter.CanAttempt(context))
        {
            return new(
                null,
                MailerJsonResults.Error(
                    MailerErrorCodes.AuthenticationRateLimited,
                    StatusCodes.Status429TooManyRequests));
        }

        var token = ReadBearerToken(context.Request);
        AuthenticatedApiKey? identity;
        try
        {
            identity = await senders.AuthenticateAsync(token, cancellationToken);
        }
        catch (Exception ex) when (MailRequestHttpErrorMapper.IsStorageFullDatabaseException(ex))
        {
            return new(null, MailRequestHttpErrorMapper.StorageFull());
        }
        catch (Exception ex) when (MailRequestHttpErrorMapper.IsTransientDatabaseException(ex))
        {
            return new(null, MailRequestHttpErrorMapper.ServiceUnavailable());
        }
        catch (SqliteException)
        {
            return new(null, MailRequestHttpErrorMapper.ServiceUnavailable());
        }

        if (identity is not null)
        {
            return new(identity, null);
        }

        var error = rateLimiter.TryConsume(context)
            ? MailerJsonResults.Error(MailerErrorCodes.Unauthorized, StatusCodes.Status401Unauthorized)
            : MailerJsonResults.Error(
                MailerErrorCodes.AuthenticationRateLimited,
                StatusCodes.Status429TooManyRequests);
        return new(null, error);
    }
}

public readonly record struct ApiKeyAuthorizationResult(
    AuthenticatedApiKey? Identity,
    IResult? Error);
