using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.AspNetCore.Antiforgery;

namespace Amane.Mailer.Admin;

public static class AdminGoogleSettingsPage
{
    public const string PagePath = "/admin/auth-settings";

    internal const string ManagedCutoverSecretRequiredMessage =
        "Enabling Google Login while claiming managed configuration requires a new Client Secret.";

    public static async Task<IResult> RenderAsync(
        HttpContext context,
        AdminUserRepository userRepository,
        AdminDeadLetterCountCache deadLetterCountCache,
        MailRequestRepository mailRequestRepository,
        InstanceConfigurationRepository instanceConfigurationRepository,
        AdminGoogleOptions googleOptions,
        IAntiforgery antiforgery,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var accessResult = await AdminManagedConfigurationAuthorization.RequireInstanceOwnerAsync(
            context,
            userRepository,
            cancellationToken);
        if (accessResult.Error is not null)
            return accessResult.Error;

        var access = accessResult.Access!;
        var deadLetterCount = await deadLetterCountCache.GetCountAsync(
            mailRequestRepository,
            access.AllowedTenantIdsForQuery,
            cancellationToken);
        var instanceConfiguration = await instanceConfigurationRepository.GetAsync(cancellationToken);
        var csrfToken = HtmlEncoder.Default.Encode(
            antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty);
        var redirectUri = BuildRedirectUri(context);
        var secretPath = AdminGoogleSecretStore.ResolveSecretPath(configuration);
        var flash = context.Request.Query["saved"].ToString() is "1"
            ? "設定を保存しました。変更を有効にするには Mailer を再起動してください。"
            : null;

        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(
            RenderHtml(
                access,
                deadLetterCount,
                instanceConfiguration,
                googleOptions,
                secretPath,
                redirectUri,
                csrfToken,
                flash),
            "text/html; charset=utf-8");
    }

    public static async Task<IResult> SaveAsync(
        HttpContext context,
        InstanceConfigurationRepository instanceConfigurationRepository,
        AdminUserRepository userRepository,
        AdminAuditRepository auditRepository,
        MailerAdminOptions adminOptions,
        IAntiforgery antiforgery,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!await ValidateAntiforgeryAsync(context, antiforgery))
            return Results.BadRequest("Invalid CSRF token.");

        var accessResult = await AdminManagedConfigurationAuthorization.RequireInstanceOwnerAsync(
            context,
            userRepository,
            cancellationToken);
        if (accessResult.Error is not null)
            return accessResult.Error;

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            return Results.BadRequest("Invalid form body.");
        }

        if (!HasConfirmation(form))
            return Results.BadRequest("Explicit confirmation is required.");

        var current = await instanceConfigurationRepository.GetAsync(cancellationToken);
        if (current?.InitializedAt is null)
            return Results.Conflict();

        var enabled = IsChecked(form, "google_login_enabled");
        var clientId = form["client_id"].ToString().Trim();
        var newSecret = form["client_secret"].ToString();
        var secretPath = AdminGoogleSecretStore.ResolveSecretPath(configuration);
        var secretWasWritten = false;
        var managedClaimed = !string.IsNullOrWhiteSpace(current.GoogleConfiguredAt);

        if (RequiresNewSecretForManagedCutover(
                managedClaimed,
                enabled,
                newSecret,
                current.GoogleClientSecretRef,
                secretPath))
        {
            await WriteAuditAsync(
                context,
                auditRepository,
                adminOptions,
                loggerFactory,
                timeProvider,
                enabled,
                clientId,
                secretWasWritten,
                AdminAuditLog.Results.Failure,
                AdminAuditLog.ErrorCodes.OperationFailed,
                cancellationToken);
            return Results.BadRequest(ManagedCutoverSecretRequiredMessage);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(newSecret))
            {
                AdminGoogleSecretStore.WriteSecret(secretPath, newSecret);
                secretWasWritten = true;
            }

            var secretRefForUpdate = ResolveSecretRefForUpdate(
                secretWasWritten,
                secretPath,
                current.GoogleClientSecretRef);

            if (!await instanceConfigurationRepository.SetGoogleLoginSettingsAsync(
                    enabled,
                    clientId.Length > 0 ? clientId : null,
                    secretRefForUpdate,
                    cancellationToken))
            {
                await WriteAuditAsync(
                    context,
                    auditRepository,
                    adminOptions,
                    loggerFactory,
                    timeProvider,
                    enabled,
                    clientId,
                    secretWasWritten,
                    AdminAuditLog.Results.Failure,
                    AdminAuditLog.ErrorCodes.OperationFailed,
                    cancellationToken);
                return Results.Conflict();
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or Operations.SecretOperationException
            or Operations.SecureFileWriteException)
        {
            await WriteAuditAsync(
                context,
                auditRepository,
                adminOptions,
                loggerFactory,
                timeProvider,
                enabled,
                clientId,
                secretWasWritten,
                AdminAuditLog.Results.Failure,
                AdminAuditLog.ErrorCodes.OperationFailed,
                cancellationToken);
            return Results.Conflict();
        }

        await WriteAuditAsync(
            context,
            auditRepository,
            adminOptions,
            loggerFactory,
            timeProvider,
            enabled,
            clientId,
            secretWasWritten,
            AdminAuditLog.Results.Success,
            null,
            cancellationToken);

        context.Response.Headers.CacheControl = "no-store";
        return new SeeOtherRedirectResult($"{PagePath}?saved=1");
    }

    private static string RenderHtml(
        AdminTenantAccess access,
        int deadLetterCount,
        InstanceConfigurationRow? instanceConfiguration,
        AdminGoogleOptions runtimeOptions,
        string secretPath,
        string redirectUri,
        string csrfToken,
        string? flash)
    {
        var html = new StringBuilder();
        AdminLayout.AppendDocumentStart(
            html,
            "認証設定 - Amane Admin",
            AdminNavItem.AuthSettings,
            deadLetterCount,
            access);

        html.AppendLine("                <section class=\"ops-section\" aria-label=\"認証設定\">");
        html.AppendLine("                  <h1 class=\"ops-heading\">認証設定</h1>");
        html.AppendLine("                  <p class=\"ops-description\">Admin の認証方法を管理します。Password Login は常に利用可能です。Google Login は追加の認証方法です。</p>");
        if (flash is not null)
        {
            html.Append("                  <p class=\"ops-meta\" role=\"status\">");
            html.Append(Html(flash));
            html.AppendLine("</p>");
        }

        html.AppendLine("                </section>");

        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Password Login\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">Password Login</h2>");
        html.AppendLine("                  <p class=\"ops-description\">Username / Password による既存ログインは常に有効です。Google 設定の不備や障害でも Password Login は維持されます。</p>");
        html.AppendLine("                  <dl class=\"ops-dl\">");
        AppendDefinition(html, "Status", "always available");
        html.AppendLine("                  </dl>");
        html.AppendLine("                </section>");

        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Google Login\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">Google Login</h2>");
        html.AppendLine("                  <p class=\"ops-description\">Google OAuth Client は Google Cloud Console で作成し、Redirect URI を登録してください。Client Secret は保存後に再表示されません。</p>");

        if (instanceConfiguration is null)
        {
            html.AppendLine("                  <p class=\"ops-empty\">Managed instance configuration is unavailable.</p>");
            html.AppendLine("                </section>");
            AdminLayout.AppendDocumentEnd(html);
            return html.ToString();
        }

        var managedClaimed = !string.IsNullOrWhiteSpace(instanceConfiguration.GoogleConfiguredAt);
        var secretConfigured = AdminGoogleSecretStore.IsSecretConfigured(
            instanceConfiguration.GoogleClientSecretRef)
            || AdminGoogleSecretStore.IsSecretConfigured(secretPath);
        var formEnabled = managedClaimed
            ? instanceConfiguration.GoogleLoginEnabled
            : runtimeOptions.Enabled;
        var formClientId = managedClaimed
            ? instanceConfiguration.GoogleClientId ?? string.Empty
            : runtimeOptions.ClientId;

        html.AppendLine("                  <dl class=\"ops-dl\">");
        AppendDefinition(
            html,
            "Runtime status",
            runtimeOptions.Enabled ? "enabled (active)" : "disabled / incomplete");
        AppendDefinition(
            html,
            "Configuration authority",
            managedClaimed
                ? "Admin UI managed configuration"
                : "legacy env (until first save)");
        AppendDefinition(
            html,
            "Client Secret",
            secretConfigured
                ? "設定済み"
                : managedClaimed ? "未設定" : "managed 未設定");
        AppendDefinition(html, "Redirect URI", redirectUri);
        html.AppendLine("                  </dl>");

        if (!managedClaimed)
        {
            html.AppendLine("                  <p class=\"ops-meta\">この保存で managed configuration が authority になります。再起動後は legacy env の Google 設定は使われません。legacy env の Client Secret は表示せず、ファイルへもコピーしません。Google Login を有効なまま切り替えるには新しい Client Secret が必要です。無効への切り替えは Client Secret なしで保存できます。</p>");
        }

        html.AppendLine("                  <form method=\"post\" action=\"/admin/auth-settings\" class=\"stack-form\">");
        html.Append("                    <input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"");
        html.Append(csrfToken);
        html.AppendLine("\">");

        html.AppendLine("                    <label>");
        html.Append("                      <input type=\"checkbox\" name=\"google_login_enabled\" value=\"1\"");
        if (formEnabled)
            html.Append(" checked");
        html.AppendLine("> Google Login を使用する");
        html.AppendLine("                    </label>");

        html.AppendLine("                    <label>");
        html.AppendLine("                      <span>Client ID</span>");
        html.Append("                      <input name=\"client_id\" type=\"text\" autocomplete=\"off\" spellcheck=\"false\" value=\"");
        html.Append(Html(formClientId));
        html.AppendLine("\" placeholder=\"xxxxxxxx.apps.googleusercontent.com\">");
        html.AppendLine("                    </label>");

        html.AppendLine("                    <label>");
        html.Append("                      <span>Client Secret");
        if (secretConfigured)
            html.Append("（設定済み — 変更時のみ入力）");
        else if (!managedClaimed)
            html.Append("（managed 未設定 — 有効なまま切り替える場合は入力）");
        else
            html.Append("（未設定）");
        html.AppendLine("</span>");
        html.AppendLine("                      <input name=\"client_secret\" type=\"password\" autocomplete=\"new-password\" spellcheck=\"false\" value=\"\">");
        html.AppendLine("                    </label>");

        html.AppendLine("                    <label>");
        html.AppendLine("                      <span>Redirect URI（Google Cloud Console に登録）</span>");
        html.Append("                      <input type=\"text\" readonly value=\"");
        html.Append(Html(redirectUri));
        html.AppendLine("\">");
        html.AppendLine("                    </label>");

        html.AppendLine("                    <label><input type=\"checkbox\" name=\"confirmation\" value=\"confirm\" required> Google Login 設定を保存することを確認します。</label>");
        html.AppendLine("                    <button type=\"submit\">保存</button>");
        html.AppendLine("                  </form>");
        html.AppendLine("                  <p class=\"ops-meta\">保存後は Mailer container / process の再起動が必要です。再起動までは Runtime status は変わりません。</p>");
        html.AppendLine("                </section>");

        AdminLayout.AppendDocumentEnd(html);
        return html.ToString();
    }

    internal static string? ResolveSecretRefForUpdate(
        bool secretWasWritten,
        string secretPath,
        string? currentSecretRef)
    {
        if (secretWasWritten)
            return secretPath;

        if (AdminGoogleSecretStore.IsSecretConfigured(currentSecretRef))
            return currentSecretRef;

        if (AdminGoogleSecretStore.IsSecretConfigured(secretPath))
            return secretPath;

        return null;
    }

    private static bool RequiresNewSecretForManagedCutover(
        bool managedClaimed,
        bool enabled,
        string newSecret,
        string? currentSecretRef,
        string secretPath) =>
        !managedClaimed
        && enabled
        && string.IsNullOrWhiteSpace(newSecret)
        && !AdminGoogleSecretStore.IsSecretConfigured(currentSecretRef)
        && !AdminGoogleSecretStore.IsSecretConfigured(secretPath);

    internal static string BuildRedirectUri(HttpContext context)
    {
        var request = context.Request;
        var host = request.Host.Value;
        if (string.IsNullOrWhiteSpace(host))
            host = "localhost";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{request.Scheme}://{host}{AdminGoogleAuthenticationConstants.CallbackPath}");
    }

    private static async Task WriteAuditAsync(
        HttpContext context,
        AdminAuditRepository auditRepository,
        MailerAdminOptions options,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        bool enabled,
        string clientId,
        bool secretUpdated,
        string result,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        var auditEvent = new AdminAuditEvent
        {
            EventType = AdminAuditLog.EventTypes.InstanceGoogleLoginSettingsUpdated,
            Actor = AdminAuditLog.ResolveActor(context),
            OccurredAt = timeProvider.GetUtcNow(),
            SourceIp = options.ResolveAuditSourceIp(AdminAuditLog.ResolveSourceIp(context)),
            UserAgentSummary = AdminAuditLog.SummarizeUserAgent(context),
            TargetType = AdminAuditLog.TargetTypes.InstanceConfiguration,
            TargetId = "1",
            Result = result,
            ErrorCode = errorCode,
            // Never record Client Secret. Client ID presence is non-secret ops metadata.
            FieldName = string.Create(
                CultureInfo.InvariantCulture,
                $"google_login;enabled={enabled};client_id_set={!string.IsNullOrWhiteSpace(clientId)};secret_updated={secretUpdated}"),
        };
        await AdminAuditLog.WriteBestEffortAsync(
            auditRepository,
            loggerFactory.CreateLogger(AdminAuditLog.LoggerCategory),
            auditEvent,
            cancellationToken);
    }

    private static bool HasConfirmation(IFormCollection form) =>
        string.Equals(form["confirmation"].ToString(), "confirm", StringComparison.Ordinal);

    private static bool IsChecked(IFormCollection form, string name) =>
        string.Equals(form[name].ToString(), "1", StringComparison.Ordinal)
        || string.Equals(form[name].ToString(), "on", StringComparison.OrdinalIgnoreCase)
        || string.Equals(form[name].ToString(), "true", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> ValidateAntiforgeryAsync(
        HttpContext context,
        IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static void AppendDefinition(StringBuilder html, string term, string value)
    {
        html.Append("                    <dt>");
        html.Append(Html(term));
        html.AppendLine("</dt>");
        html.Append("                    <dd><code>");
        html.Append(Html(value));
        html.AppendLine("</code></dd>");
    }

    private static string Html(string value) => HtmlEncoder.Default.Encode(value);

    private sealed class SeeOtherRedirectResult(string url) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status303SeeOther;
            httpContext.Response.Headers.Location = url;
            return Task.CompletedTask;
        }
    }
}
