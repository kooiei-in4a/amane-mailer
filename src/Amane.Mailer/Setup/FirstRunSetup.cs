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
    public const string BootstrapShowCommand =
        "docker compose --env-file .env -f compose.yml exec mailer /app/Amane.Mailer setup bootstrap show";
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
        AdminUserRepository userRepository,
        SenderRepository senderRepository,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        if (!IsHttps(context))
        {
            return Results.NotFound();
        }

        var configuration = await configurationRepository.GetAsync(cancellationToken);
        if (configuration is null || configuration.InitializedAt is not null)
        {
            return Results.NotFound();
        }

        var tokens = antiforgery.GetAndStoreTokens(context);
        var requestToken = HtmlEncoder.Default.Encode(tokens.RequestToken ?? string.Empty);
        var progress = new SetupProgress(
            IsAcsConfigured(configuration),
            await userRepository.GetActiveInstanceOwnerUsernameAsync(cancellationToken),
            await senderRepository.FindFirstEnabledAsync(cancellationToken));
        var authenticated = context.User.Identity?.IsAuthenticated == true;
        var body = new StringBuilder();
        AppendSetupProgress(body, progress);
        if (authenticated)
        {
            body.AppendLine("<p class=\"setup-authenticated\">セットアップ認証済みです。</p>");
            AppendAcsSection(body, requestToken, progress);
            AppendAdminSection(body, requestToken, progress);
            AppendSenderSection(body, requestToken, progress);
            AppendFinalizeSection(body, requestToken, progress);
        }
        else
        {
            AppendBootstrapSection(body, requestToken);
        }

        const string style = """
            :root { color-scheme: light; }
            body { background: #f6f7fb; color: #20242b; font-family: system-ui, sans-serif; line-height: 1.55; margin: 0; }
            main { box-sizing: border-box; max-width: 48rem; margin: 0 auto; padding: 2rem 1rem 3rem; }
            h1 { font-size: 1.6rem; line-height: 1.25; margin: 0 0 1.5rem; }
            h2 { align-items: center; display: flex; flex-wrap: wrap; font-size: 1.15rem; gap: .5rem; line-height: 1.35; margin: 0 0 .75rem; }
            p { margin: .65rem 0; }
            .setup-section { background: #fff; border: 1px solid #d6dbe5; border-radius: .6rem; margin: 1rem 0; padding: 1rem; }
            .setup-progress ol, .setup-summary-list { list-style: none; margin: .5rem 0 0; padding: 0; }
            .setup-progress li, .setup-summary-list li { align-items: center; display: flex; flex-wrap: wrap; gap: .5rem 1rem; justify-content: space-between; padding: .35rem 0; }
            .step-number { color: #4a5568; font-variant-numeric: tabular-nums; }
            .setup-status { border-radius: 999px; font-size: .9rem; font-weight: 700; padding: .1rem .55rem; white-space: nowrap; }
            .setup-status.is-configured { background: #e3f5e9; color: #146c36; }
            .setup-status.is-pending { background: #fff1d6; color: #7a4b00; }
            .setup-help, .setup-note { color: #4a5568; font-size: .95rem; }
            .setup-summary { background: #f3f6fa; border-left: .25rem solid #718096; padding: .65rem .75rem; }
            .setup-summary dl { margin: .5rem 0 0; }
            .setup-summary dl > div { display: grid; gap: .25rem 1rem; grid-template-columns: minmax(8rem, 12rem) 1fr; padding: .25rem 0; }
            .setup-summary dt { font-weight: 700; }
            .setup-summary dd { margin: 0; overflow-wrap: anywhere; }
            form { background: #fafbfe; border: 1px solid #e0e4ec; border-radius: .45rem; display: grid; gap: .65rem; margin: 1rem 0 0; padding: .85rem; }
            label { display: grid; gap: .25rem; font-weight: 650; }
            label small { color: #4a5568; font-size: .9rem; font-weight: 400; }
            input, button { box-sizing: border-box; font: inherit; min-height: 2.5rem; padding: .5rem .65rem; width: 100%; }
            button { background: #2457a6; border: 0; border-radius: .35rem; color: #fff; cursor: pointer; font-weight: 700; }
            code, pre { font-family: ui-monospace, SFMono-Regular, Consolas, monospace; }
            code { background: #eef1f6; border-radius: .2rem; padding: .08rem .25rem; }
            pre { background: #20242b; border-radius: .4rem; color: #f7f8fa; margin: .6rem 0; overflow-x: auto; padding: .75rem; white-space: pre-wrap; overflow-wrap: anywhere; }
            pre code { background: transparent; padding: 0; }
            .setup-authenticated { color: #146c36; font-weight: 700; }
            @media (max-width: 34rem) {
              main { padding-top: 1.25rem; }
              .setup-summary dl > div { display: block; }
              .setup-summary dd { margin-top: .15rem; }
            }
            """;
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
              <main>
                <h1>Amane Mailer 初回セットアップ</h1>
                {{body}}
              </main>
            </body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static void AppendSetupProgress(StringBuilder html, SetupProgress progress)
    {
        html.AppendLine("<section class=\"setup-section setup-progress\" aria-labelledby=\"setup-progress-heading\">");
        html.AppendLine("  <h2 id=\"setup-progress-heading\">セットアップ状況</h2>");
        html.AppendLine("  <ol>");
        AppendProgressItem(html, "ACS接続設定", progress.AcsConfigured);
        AppendProgressItem(html, "管理者アカウント", progress.AdminConfigured);
        AppendProgressItem(html, "送信元", progress.SenderConfigured);
        AppendProgressItem(html, "セットアップ完了", false, "未完了");
        html.AppendLine("  </ol>");
        html.AppendLine("</section>");
    }

    private static void AppendProgressItem(
        StringBuilder html,
        string label,
        bool configured,
        string pendingLabel = "未設定")
    {
        html.Append("    <li><span>").Append(label).Append("</span>");
        AppendStatus(html, configured, pendingLabel);
        html.AppendLine("</li>");
    }

    private static void AppendStatus(
        StringBuilder html,
        bool configured,
        string pendingLabel = "未設定")
    {
        if (configured)
        {
            html.Append("<span class=\"setup-status is-configured\">設定済み</span>");
            return;
        }

        html.Append("<span class=\"setup-status is-pending\">")
            .Append(pendingLabel)
            .Append("</span>");
    }

    private static void AppendBootstrapSection(StringBuilder html, string requestToken)
    {
        html.AppendLine("<section class=\"setup-section\" aria-labelledby=\"bootstrap-heading\">");
        html.AppendLine("  <h2 id=\"bootstrap-heading\">Bootstrap token</h2>");
        html.AppendLine("  <p>初回セットアップ用のBootstrap tokenを入力してください。</p>");
        html.AppendLine("  <p class=\"setup-help\">分からない場合は、Amane Mailerを起動しているサーバー上で次のコマンドを実行して確認できます。</p>");
        html.Append("  <pre><code>")
            .Append(HtmlEncoder.Default.Encode(FirstRunSetupConstants.BootstrapShowCommand))
            .AppendLine("</code></pre>");
        html.AppendLine("  <p class=\"setup-note\">Bootstrap tokenは秘密情報です。初回セットアップが完了すると利用できなくなり、画面にも再表示されません。</p>");
        html.AppendLine("  <form method=\"post\" action=\"/setup/auth\">");
        AppendCsrfInput(html, requestToken);
        html.AppendLine("    <label for=\"bootstrap-token\">Bootstrap token</label>");
        html.AppendLine("    <input id=\"bootstrap-token\" name=\"bootstrap_token\" type=\"password\" autocomplete=\"off\" required>");
        html.AppendLine("    <button type=\"submit\">認証</button>");
        html.AppendLine("  </form>");
        html.AppendLine("</section>");
    }

    private static void AppendAcsSection(
        StringBuilder html,
        string requestToken,
        SetupProgress progress)
    {
        html.AppendLine("<section class=\"setup-section\" aria-labelledby=\"acs-heading\">");
        AppendSectionHeading(html, "acs-heading", "1.", "Azure Communication Services（ACS）の接続設定", progress.AcsConfigured);
        html.AppendLine("  <p>Amane MailerがAzure Communication Servicesを使ってメールを送信するための接続情報です。</p>");
        html.AppendLine("  <p class=\"setup-help\">入力例: <code>endpoint=https://xxxxx.communication.azure.com/;accesskey=xxxxxxxx</code></p>");
        html.AppendLine("  <p class=\"setup-help\">Azure Portalでの確認場所:</p>");
        html.AppendLine("  <pre><code>Azure Portal");
        html.AppendLine("→ Communication Services");
        html.AppendLine("→ 使用するリソース");
        html.AppendLine("→ 設定");
        html.AppendLine("→ キー");
        html.AppendLine("→ Primary key の Connection string</code></pre>");
        html.AppendLine("  <p class=\"setup-note\"><code>accesskey</code>は秘密情報です。入力したconnection stringやaccesskeyは、登録後も画面に再表示されません。</p>");

        if (progress.AcsConfigured)
        {
            html.AppendLine("  <p class=\"setup-summary\"><strong>ACS接続情報:</strong> 設定済み（秘密情報のため詳細は表示しません）</p>");
        }
        else
        {
            html.AppendLine("  <p class=\"setup-help\">新しい接続情報を登録します。既存の保護済みファイルから再開する場合は空欄で送信できます。</p>");
            html.AppendLine("  <form method=\"post\" action=\"/setup/provider\">");
            AppendCsrfInput(html, requestToken);
            html.AppendLine("    <label for=\"acs-connection-string\">ACS connection string（既存の保護済みファイルを復旧する場合は空欄）</label>");
            html.AppendLine("    <input id=\"acs-connection-string\" name=\"connection_string\" type=\"password\" autocomplete=\"off\">");
            html.AppendLine("    <button type=\"submit\">ACSを登録</button>");
            html.AppendLine("  </form>");
        }

        html.AppendLine("</section>");
    }

    private static void AppendAdminSection(
        StringBuilder html,
        string requestToken,
        SetupProgress progress)
    {
        html.AppendLine("<section class=\"setup-section\" aria-labelledby=\"admin-heading\">");
        AppendSectionHeading(html, "admin-heading", "2.", "管理者アカウント", progress.AdminConfigured);
        html.AppendLine("  <p>Amane Mailerの管理画面へログインするための最初の管理者アカウントを作成します。</p>");

        if (progress.AdminUsername is not null)
        {
            html.AppendLine("  <div class=\"setup-summary\">");
            html.AppendLine("    <dl>");
            html.Append("      <div><dt>Username</dt><dd>")
                .Append(HtmlEncoder.Default.Encode(progress.AdminUsername))
                .AppendLine("</dd></div>");
            html.AppendLine("      <div><dt>Password</dt><dd>設定済み（表示しません）</dd></div>");
            html.AppendLine("    </dl>");
            html.AppendLine("  </div>");
        }
        else
        {
            html.AppendLine("  <form method=\"post\" action=\"/setup/admin\">");
            AppendCsrfInput(html, requestToken);
            html.AppendLine("    <label for=\"admin-username\">Admin username<small>管理画面へのログインに使うユーザー名</small></label>");
            html.AppendLine("    <input id=\"admin-username\" name=\"username\" autocomplete=\"username\" required>");
            html.AppendLine("    <label for=\"admin-password\">Password<small>管理画面へのログインに使うパスワード</small></label>");
            html.AppendLine("    <input id=\"admin-password\" name=\"password\" type=\"password\" autocomplete=\"new-password\" required>");
            html.AppendLine("    <label for=\"admin-password-confirmation\">Confirm password<small>確認のため同じパスワードを再入力</small></label>");
            html.AppendLine("    <input id=\"admin-password-confirmation\" name=\"confirmation\" type=\"password\" autocomplete=\"new-password\" required>");
            html.AppendLine("    <button type=\"submit\">Adminを登録</button>");
            html.AppendLine("  </form>");
        }

        html.AppendLine("</section>");
    }

    private static void AppendSenderSection(
        StringBuilder html,
        string requestToken,
        SetupProgress progress)
    {
        html.AppendLine("<section class=\"setup-section\" aria-labelledby=\"sender-heading\">");
        AppendSectionHeading(html, "sender-heading", "3.", "送信元メールアドレス", progress.SenderConfigured);
        html.AppendLine("  <p>Amane Mailerからメールを送信するときに使用する送信元を登録します。</p>");
        html.AppendLine("  <p class=\"setup-help\"><strong>Sender email:</strong> Azure Communication Services Emailで利用可能な送信元メールアドレス</p>");
        html.AppendLine("  <p class=\"setup-help\"><strong>Display name:</strong> 受信者に表示される送信者名</p>");
        html.AppendLine("  <p class=\"setup-help\">入力例: <code>Sender email: noreply@example.com</code> / <code>Display name: Amane System</code></p>");

        if (progress.Sender is not null)
        {
            html.AppendLine("  <div class=\"setup-summary\">");
            html.AppendLine("    <dl>");
            html.Append("      <div><dt>Sender email</dt><dd>")
                .Append(HtmlEncoder.Default.Encode(progress.Sender.Email))
                .AppendLine("</dd></div>");
            html.Append("      <div><dt>Display name</dt><dd>")
                .Append(HtmlEncoder.Default.Encode(progress.Sender.DisplayName ?? "（表示名なし）"))
                .AppendLine("</dd></div>");
            html.AppendLine("    </dl>");
            html.AppendLine("  </div>");
        }
        else
        {
            html.AppendLine("  <form method=\"post\" action=\"/setup/sender\">");
            AppendCsrfInput(html, requestToken);
            html.AppendLine("    <label for=\"sender-email\">Sender email</label>");
            html.AppendLine("    <input id=\"sender-email\" name=\"email\" type=\"email\" autocomplete=\"email\" required>");
            html.AppendLine("    <label for=\"sender-display-name\">Display name</label>");
            html.AppendLine("    <input id=\"sender-display-name\" name=\"display_name\" autocomplete=\"organization\" required>");
            html.AppendLine("    <button type=\"submit\">Senderを登録</button>");
            html.AppendLine("  </form>");
        }

        html.AppendLine("</section>");
    }

    private static void AppendFinalizeSection(
        StringBuilder html,
        string requestToken,
        SetupProgress progress)
    {
        html.AppendLine("<section class=\"setup-section\" aria-labelledby=\"finalize-heading\">");
        AppendSectionHeading(html, "finalize-heading", "4.", "セットアップ完了", configured: false, pendingStatus: "未完了");
        html.AppendLine("  <p>セットアップを完了する前に、現在の状態を確認してください。</p>");
        html.AppendLine("  <ul class=\"setup-summary-list\">");
        AppendProgressItem(html, "ACS接続設定", progress.AcsConfigured);
        AppendProgressItem(html, "管理者アカウント", progress.AdminConfigured);
        AppendProgressItem(html, "送信元", progress.SenderConfigured);
        html.AppendLine("  </ul>");
        html.AppendLine(progress.IsReadyToFinalize
            ? "  <p class=\"setup-authenticated\">必要な設定がすべて完了しました。</p>"
            : "  <p class=\"setup-note\">未設定の項目を入力してから、セットアップを完了してください。</p>");
        html.AppendLine("  <form method=\"post\" action=\"/setup/finalize\">");
        AppendCsrfInput(html, requestToken);
        html.AppendLine("    <button type=\"submit\">セットアップを完了</button>");
        html.AppendLine("  </form>");
        html.AppendLine("</section>");
    }

    private static void AppendSectionHeading(
        StringBuilder html,
        string id,
        string step,
        string title,
        bool configured,
        string pendingStatus = "未設定")
    {
        html.Append("  <h2 id=\"").Append(id).Append("\"><span class=\"step-number\">")
            .Append(step)
            .Append("</span> ")
            .Append(title)
            .Append(' ');
        AppendStatus(html, configured, pendingStatus);
        html.AppendLine("</h2>");
    }

    private static void AppendCsrfInput(StringBuilder html, string requestToken) =>
        html.Append("    <input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"")
            .Append(requestToken)
            .AppendLine("\">");

    private static bool IsAcsConfigured(InstanceConfigurationRow configuration)
    {
        var secretPath = configuration.ProviderSecretRef;
        if (!string.Equals(configuration.ProviderType, "acs", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(secretPath)
            || string.IsNullOrWhiteSpace(configuration.ProviderConfiguredAt))
        {
            return false;
        }

        // Match the existing finalization gate without exposing or retaining the protected value
        // in the page model. No endpoint extraction or secret value is added to the page.
        return FirstRunSetupStorage.TryReadValidAcsSecret(secretPath, out _);
    }

    private sealed record SetupProgress(
        bool AcsConfigured,
        string? AdminUsername,
        SenderIdentity? Sender)
    {
        public bool AdminConfigured => AdminUsername is not null;
        public bool SenderConfigured => Sender is not null;
        public bool IsReadyToFinalize => AcsConfigured && AdminConfigured && SenderConfigured;
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
