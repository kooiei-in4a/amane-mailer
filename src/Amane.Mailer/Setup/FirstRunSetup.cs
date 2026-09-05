using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Azure.Communication.Email;
using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Identity;
using Amane.Mailer.Operations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;

namespace Amane.Mailer.Setup;

internal static class FirstRunSetupConstants
{
    public const string AuthenticationScheme = "AmaneFirstRunSetup";
    public const string AuthenticationCookieName = "__Host-amane-setup-auth";
    public const string TokenFormField = "bootstrap_token";
    public const string AcsSecretPathKey = "MAILER_SETUP_ACS_SECRET_PATH";
    public const string AcsSecretPathConfigurationKey = "Mailer:Setup:AcsSecretPath";
    public const string BootstrapTokenPathKey = "MAILER_BOOTSTRAP_TOKEN_PATH";
    public const string BootstrapTokenPathConfigurationKey = "Mailer:Setup:BootstrapTokenPath";
    public const string DefaultBootstrapTokenPath = "/app/data/bootstrap/setup_token";
    public const string DefaultAcsSecretDirectory = "/app/data/secrets/acs";
    public static string DefaultAcsSecretPath =>
        Path.Combine(DefaultAcsSecretDirectory, AcsSecretFileNames.CanonicalFileName);
}

internal sealed class BootstrapTokenStore(IConfiguration configuration)
{
    private const int TokenSizeBytes = 32;
    private const int TokenTextLength = 43;
    private readonly HostSetupFileSystem _fileSystem = new();

    public string TokenPath => ResolvePath(
        configuration,
        FirstRunSetupConstants.BootstrapTokenPathConfigurationKey,
        FirstRunSetupConstants.BootstrapTokenPathKey,
        FirstRunSetupConstants.DefaultBootstrapTokenPath);

    public string EnsureExists()
    {
        var path = TokenPath;
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Bootstrap token path is invalid.");
        _fileSystem.CreateOwnerOnlyDirectory(directory);
        FileSystemSafetyGuard.EnsureDirectoryIsSafe(directory);
        FileSystemSafetyGuard.EnsureTargetFileIsSafeIfExists(path);

        if (File.Exists(path))
        {
            return ReadValidated(path)
                ?? throw new InvalidOperationException("Bootstrap token file is invalid.");
        }

        var bytes = RandomNumberGenerator.GetBytes(TokenSizeBytes);
        try
        {
            var token = Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
            try
            {
                SecureFileCreate.WriteAllTextCreateNew(temporaryPath, token);
                try
                {
                    File.Move(temporaryPath, path, overwrite: false);
                    _fileSystem.FlushDirectory(directory);
                    return token;
                }
                catch (IOException) when (File.Exists(path))
                {
                    return ReadValidated(path)
                        ?? throw new InvalidOperationException("Bootstrap token file is invalid.");
                }
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public bool TryRead(out string token)
    {
        token = string.Empty;
        var value = ReadValidated(TokenPath);
        if (value is null)
        {
            return false;
        }

        token = value;
        return true;
    }

    public bool IsValid(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !TryRead(out var expected))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(candidate.Trim());
        var expectedDigest = SHA256.HashData(expectedBytes);
        var actualDigest = SHA256.HashData(actualBytes);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expectedDigest, actualDigest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(actualBytes);
            CryptographicOperations.ZeroMemory(expectedDigest);
            CryptographicOperations.ZeroMemory(actualDigest);
        }
    }

    public void DeleteBestEffort()
    {
        try
        {
            var path = TokenPath;
            FileSystemSafetyGuard.EnsureTargetFileIsSafeIfExists(path);
            if (File.Exists(path))
            {
                File.Delete(path);
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    try
                    {
                        _fileSystem.FlushDirectory(directory);
                    }
                    catch
                    {
                        // Cleanup is deliberately best effort after initialization commits.
                    }
                }
            }
        }
        catch
        {
            // The durable initialized bit is authoritative; a stale token is unusable after it.
        }
    }

    private string? ReadValidated(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            FileSystemSafetyGuard.EnsureTargetFileIsSafeIfExists(path);
            if (!_fileSystem.IsOwnerOnlyFile(path))
            {
                return null;
            }

            var value = File.ReadAllText(path).Trim();
            if (value.Length != TokenTextLength)
            {
                return null;
            }

            var encoded = value.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(encoded + "=");
            try
            {
                return bytes.Length == TokenSizeBytes && IsBase64Url(value) ? value : null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or FormatException
            or SecretOperationException)
        {
            return null;
        }
    }

    private static bool IsBase64Url(string value) =>
        value.All(static character =>
            character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-' or '_');

    private static string ResolvePath(
        IConfiguration configuration,
        string configurationKey,
        string environmentKey,
        string defaultPath)
    {
        var configured = configuration[configurationKey]
            ?? configuration[environmentKey];
        return string.IsNullOrWhiteSpace(configured)
            ? defaultPath
            : Path.GetFullPath(configured);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort cleanup of an uncommitted temporary file.
        }
    }
}

internal static class FirstRunSetupStorage
{
    public static string ResolveAcsSecretPath(IConfiguration configuration)
    {
        var explicitSetupPath = configuration[FirstRunSetupConstants.AcsSecretPathConfigurationKey]
            ?? configuration[FirstRunSetupConstants.AcsSecretPathKey];
        if (!string.IsNullOrWhiteSpace(explicitSetupPath))
        {
            // An explicit setup path is an operator choice, even when it is outside the
            // deployment's read-only /run/secrets mount convention.
            return Path.GetFullPath(explicitSetupPath);
        }

        var configuredRuntimePath = configuration["ACS_CONNECTION_STRING_FILE"];
        if (!string.IsNullOrWhiteSpace(configuredRuntimePath))
        {
            // The existing Compose /run/secrets/acs mount is read-only. Browser setup writes to
            // the persistent data volume unless the operator supplied an explicit setup path.
            if (configuredRuntimePath.StartsWith("/run/secrets/", StringComparison.Ordinal))
            {
                return FirstRunSetupConstants.DefaultAcsSecretPath;
            }

            return Path.GetFullPath(configuredRuntimePath);
        }

        // The deployment image's /run/secrets mount is intentionally read-only. A setup secret
        // therefore lives under the existing persistent /app/data root and is referenced from
        // SQLite after setup; no extra writable secret volume is required.
        return FirstRunSetupConstants.DefaultAcsSecretPath;
    }

    public static bool TryReadValidAcsSecret(string path, out string value)
    {
        value = string.Empty;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            FileSystemSafetyGuard.EnsureTargetFileIsSafeIfExists(path);
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            FileSystemSafetyGuard.EnsureDirectoryIsSafe(directory);
            var fileSystem = new HostSetupFileSystem();
            if (!fileSystem.IsOwnerOnlyFile(path))
            {
                return false;
            }

            var candidate = File.ReadAllText(path).Trim();
            if (!IsValidAcsConnectionString(candidate))
            {
                return false;
            }

            var resolution = MailerAcsCredential.ResolveFromPath(path);
            if (resolution.Source != MailerAcsCredentialSource.File)
            {
                return false;
            }

            value = candidate;
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or SecretOperationException)
        {
            return false;
        }
    }

    public static bool WriteAcsSecretCreateOnly(
        string path,
        string value)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("ACS secret path is invalid.");
        var fileSystem = new HostSetupFileSystem();
        fileSystem.CreateOwnerOnlyDirectory(directory);
        FileSystemSafetyGuard.EnsureDirectoryIsSafe(directory);
        FileSystemSafetyGuard.EnsureTargetFileIsSafeIfExists(path);

        if (File.Exists(path))
        {
            return TryReadValidAcsSecret(path, out _);
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
        try
        {
            SecureFileCreate.WriteAllTextCreateNew(temporaryPath, value);
            try
            {
                File.Move(temporaryPath, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                return TryReadValidAcsSecret(path, out _);
            }

            fileSystem.FlushDirectory(directory);
            return TryReadValidAcsSecret(path, out _);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Best effort cleanup of an uncommitted temporary secret.
            }
        }
    }

    public static bool IsValidAcsConnectionString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var endpoint = value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static part => part.Split('=', 2))
            .FirstOrDefault(static pair => pair.Length == 2
                && string.Equals(pair[0].Trim(), "endpoint", StringComparison.OrdinalIgnoreCase))?
            [1]
            .Trim();
        var accessKey = value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static part => part.Split('=', 2))
            .FirstOrDefault(static pair => pair.Length == 2
                && string.Equals(pair[0].Trim(), "accesskey", StringComparison.OrdinalIgnoreCase))?
            [1]
            .Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            || !string.Equals(endpointUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(endpointUri.Host)
            || string.IsNullOrWhiteSpace(accessKey))
        {
            return false;
        }

        try
        {
            _ = new EmailClient(value);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal static class FirstRunSetupEndpoints
{
    public static void AddServices(IServiceCollection services)
    {
        services.AddSingleton<BootstrapTokenStore>();
        services.AddSingleton<InstanceConfigurationRepository>();
        services.AddSingleton<SenderRepository>();
        services.AddSingleton<AdminUserRepository>();
        services.AddSingleton<ApiAuthenticationRateLimiter>();

        services
            .AddAuthentication(FirstRunSetupConstants.AuthenticationScheme)
            .AddCookie(FirstRunSetupConstants.AuthenticationScheme, cookie =>
            {
                cookie.Cookie.Name = FirstRunSetupConstants.AuthenticationCookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.Cookie.Path = "/";
                cookie.ExpireTimeSpan = TimeSpan.FromMinutes(30);
                cookie.SlidingExpiration = false;
                cookie.LoginPath = "/setup";
                cookie.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                cookie.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
        services.AddAuthorization();
        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.Cookie.Name = "__Host-amane-setup-csrf";
            antiforgery.Cookie.HttpOnly = true;
            antiforgery.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
            antiforgery.Cookie.Path = "/";
            antiforgery.FormFieldName = "__RequestVerificationToken";
            antiforgery.HeaderName = "X-CSRF-TOKEN";
        });
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/setup", RenderSetupAsync);
        app.MapPost("/setup/auth", AuthenticateAsync);
        app.MapPost("/setup/provider", ConfigureProviderAsync);
        app.MapPost("/setup/admin", ConfigureAdminAsync);
        app.MapPost("/setup/sender", ConfigureSenderAsync);
        app.MapPost("/setup/finalize", FinalizeAsync);
    }

    private static async Task<IResult> RenderSetupAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        InstanceConfigurationRepository configurationRepository,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        if (!IsHttps(context) || !await IsUninitializedAsync(configurationRepository, cancellationToken))
        {
            return Results.NotFound();
        }

        var tokens = antiforgery.GetAndStoreTokens(context);
        var requestToken = HtmlEncoder.Default.Encode(tokens.RequestToken ?? string.Empty);
        var authenticated = context.User.Identity?.IsAuthenticated == true;
        var forms = authenticated
            ? $$"""
              <p>セットアップ認証済みです。</p>
              <form method="post" action="/setup/provider">
                <input type="hidden" name="__RequestVerificationToken" value="{{requestToken}}">
                <label>ACS connection string（既存の保護済みファイルを復旧する場合は空欄） <input name="connection_string" type="password" autocomplete="off"></label>
                <button type="submit">ACSを登録</button>
              </form>
              <form method="post" action="/setup/admin">
                <input type="hidden" name="__RequestVerificationToken" value="{{requestToken}}">
                <label>Admin username <input name="username" autocomplete="username" required></label>
                <label>Password <input name="password" type="password" autocomplete="new-password" required></label>
                <label>Confirm password <input name="confirmation" type="password" autocomplete="new-password" required></label>
                <button type="submit">Adminを登録</button>
              </form>
              <form method="post" action="/setup/sender">
                <input type="hidden" name="__RequestVerificationToken" value="{{requestToken}}">
                <label>Sender email <input name="email" type="email" autocomplete="email" required></label>
                <label>Display name <input name="display_name" autocomplete="organization" required></label>
                <button type="submit">Senderを登録</button>
              </form>
              <form method="post" action="/setup/finalize">
                <input type="hidden" name="__RequestVerificationToken" value="{{requestToken}}">
                <button type="submit">セットアップを完了</button>
              </form>
              """
            : """
              <form method="post" action="/setup/auth">
                <input type="hidden" name="__RequestVerificationToken" value="TOKEN_PLACEHOLDER">
                <label>Bootstrap token <input name="bootstrap_token" type="password" autocomplete="off" required></label>
                <button type="submit">認証</button>
              </form>
              """;
        forms = forms.Replace("TOKEN_PLACEHOLDER", requestToken, StringComparison.Ordinal);
        const string style = "body{font-family:system-ui,sans-serif;max-width:42rem;margin:2rem auto;padding:0 1rem}form{display:grid;gap:.6rem;margin:1.2rem 0;padding:1rem;border:1px solid #ccd;border-radius:.4rem}input,button{font:inherit;padding:.5rem}h1{font-size:1.5rem}";
        var html = $$"""
            <!doctype html>
            <html lang="ja">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta http-equiv="Cache-Control" content="no-store">
              <title>Amane Mailer setup</title>
              <style>{{style}}</style>
            </head>
            <body>
              <h1>Amane Mailer 初回セットアップ</h1>
              {{forms}}
            </body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static async Task<IResult> AuthenticateAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        InstanceConfigurationRepository configurationRepository,
        BootstrapTokenStore tokenStore,
        ApiAuthenticationRateLimiter rateLimiter,
        CancellationToken cancellationToken)
    {
        var guard = await ValidateMutationAsync(
            context,
            antiforgery,
            configurationRepository,
            requireSetupAuthentication: false,
            cancellationToken);
        if (guard is not null)
        {
            return guard;
        }

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            return GenericError(context, StatusCodes.Status400BadRequest);
        }

        if (!rateLimiter.CanAttempt(context))
        {
            return GenericError(context, StatusCodes.Status429TooManyRequests);
        }

        var candidate = form[FirstRunSetupConstants.TokenFormField].ToString();
        if (!tokenStore.IsValid(candidate))
        {
            var status = rateLimiter.TryConsume(context)
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status429TooManyRequests;
            return GenericError(context, status);
        }

        var claims = new[] { new Claim(ClaimTypes.Name, "first-run-setup") };
        await context.SignInAsync(
            FirstRunSetupConstants.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(
                claims,
                FirstRunSetupConstants.AuthenticationScheme)),
            new AuthenticationProperties
            {
                AllowRefresh = false,
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30),
            });
        return Results.Redirect("/setup");
    }

    private static async Task<IResult> ConfigureProviderAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        InstanceConfigurationRepository configurationRepository,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var guard = await ValidateMutationAsync(
            context,
            antiforgery,
            configurationRepository,
            requireSetupAuthentication: true,
            cancellationToken);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var value = form["connection_string"].ToString().Trim();
            var current = await configurationRepository.GetAsync(cancellationToken);
            var path = current?.ProviderSecretRef
                ?? FirstRunSetupStorage.ResolveAcsSecretPath(configuration);

            // A crash after the protected file is durably created but before the metadata
            // transaction commits must be resumable without asking the operator to re-enter a
            // secret that the browser never gets to read back. Existing valid content always wins;
            // a missing file still requires a valid initial credential and create-only write.
            var secretReady = FirstRunSetupStorage.TryReadValidAcsSecret(path, out _);
            if (!secretReady)
            {
                secretReady = FirstRunSetupStorage.IsValidAcsConnectionString(value)
                    && FirstRunSetupStorage.WriteAcsSecretCreateOnly(path, value);
            }

            if (!secretReady
                || !FirstRunSetupStorage.TryReadValidAcsSecret(path, out _)
                || !await configurationRepository.ConfigureAcsAsync(path, cancellationToken))
            {
                return GenericError(context, StatusCodes.Status409Conflict);
            }

            return Results.Redirect("/setup");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return GenericError(context, StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> ConfigureAdminAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        InstanceConfigurationRepository configurationRepository,
        AdminUserRepository userRepository,
        CancellationToken cancellationToken)
    {
        var guard = await ValidateMutationAsync(
            context,
            antiforgery,
            configurationRepository,
            requireSetupAuthentication: true,
            cancellationToken);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var username = form["username"].ToString().Trim();
            var password = form["password"].ToString();
            var confirmation = form["confirmation"].ToString();
            if (username.Length is < 1 or > 128
                || password.Length is < 12 or > 1024
                || !string.Equals(password, confirmation, StringComparison.Ordinal))
            {
                return GenericError(context, StatusCodes.Status400BadRequest);
            }

            var hash = AdminPasswordHasher.Hash(password);
            var accepted = await userRepository.EnsureInstanceOwnerAsync(
                username,
                hash,
                cancellationToken);
            CryptographicOperations.ZeroMemory(Encoding.UTF8.GetBytes(password));
            CryptographicOperations.ZeroMemory(Encoding.UTF8.GetBytes(confirmation));
            return accepted
                ? Results.Redirect("/setup")
                : GenericError(context, StatusCodes.Status409Conflict);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return GenericError(context, StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> ConfigureSenderAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        InstanceConfigurationRepository configurationRepository,
        SenderRepository senderRepository,
        CancellationToken cancellationToken)
    {
        var guard = await ValidateMutationAsync(
            context,
            antiforgery,
            configurationRepository,
            requireSetupAuthentication: true,
            cancellationToken);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var email = form["email"].ToString();
            var displayName = form["display_name"].ToString();
            var normalizedEmail = SenderRepository.NormalizeEmail(email);
            var existing = await senderRepository.FindByEmailAsync(normalizedEmail, cancellationToken);
            if (existing is not null)
            {
                return Results.Redirect("/setup");
            }

            if (await senderRepository.CountAsync(cancellationToken) != 0)
            {
                return GenericError(context, StatusCodes.Status409Conflict);
            }

            await senderRepository.CreateAsync(normalizedEmail, displayName, cancellationToken);
            return Results.Redirect("/setup");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return GenericError(context, StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> FinalizeAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        InstanceConfigurationRepository configurationRepository,
        BootstrapTokenStore tokenStore,
        IHostApplicationLifetime applicationLifetime,
        CancellationToken cancellationToken)
    {
        var guard = await ValidateMutationAsync(
            context,
            antiforgery,
            configurationRepository,
            requireSetupAuthentication: true,
            cancellationToken);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            var current = await configurationRepository.GetAsync(cancellationToken);
            if (current is null
                || !string.Equals(current.ProviderType, "acs", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(current.ProviderSecretRef)
                || !FirstRunSetupStorage.TryReadValidAcsSecret(current.ProviderSecretRef, out _))
            {
                return GenericError(context, StatusCodes.Status409Conflict);
            }

            if (!await configurationRepository.FinalizeAsync(cancellationToken))
            {
                return GenericError(context, StatusCodes.Status409Conflict);
            }

            context.Response.OnCompleted(() =>
            {
                tokenStore.DeleteBestEffort();
                applicationLifetime.StopApplication();
                return Task.CompletedTask;
            });
            return Results.Text("セットアップを完了しました。サービスを再起動してください。", "text/plain; charset=utf-8");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return GenericError(context, StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult?> ValidateMutationAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        InstanceConfigurationRepository configurationRepository,
        bool requireSetupAuthentication,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        if (!IsHttps(context) || !HasSameOrigin(context))
        {
            return GenericError(context, StatusCodes.Status403Forbidden);
        }

        if (!await IsUninitializedAsync(configurationRepository, cancellationToken))
        {
            return Results.NotFound();
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return GenericError(context, StatusCodes.Status400BadRequest);
        }

        if (requireSetupAuthentication)
        {
            var authentication = await context.AuthenticateAsync(
                FirstRunSetupConstants.AuthenticationScheme);
            if (!authentication.Succeeded || authentication.Principal?.Identity?.IsAuthenticated != true)
            {
                return GenericError(context, StatusCodes.Status401Unauthorized);
            }
        }

        return null;
    }

    private static async Task<bool> IsUninitializedAsync(
        InstanceConfigurationRepository configurationRepository,
        CancellationToken cancellationToken)
    {
        var row = await configurationRepository.GetAsync(cancellationToken);
        return row is not null && row.InitializedAt is null;
    }

    private static bool IsHttps(HttpContext context) => context.Request.IsHttps;

    private static bool HasSameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
            || originUri.UserInfo.Length != 0
            || originUri.AbsolutePath != "/"
            || originUri.Query.Length != 0
            || !string.Equals(originUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = context.Request.Host;
        var expectedPort = host.Port ?? 443;
        return string.Equals(originUri.Host, host.Host, StringComparison.OrdinalIgnoreCase)
            && originUri.Port == expectedPort;
    }

    private static IResult GenericError(HttpContext context, int statusCode)
    {
        SetNoStore(context);
        return Results.Text(
            "セットアップ要求を処理できませんでした。",
            "text/plain; charset=utf-8",
            statusCode: statusCode);
    }

    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
}
