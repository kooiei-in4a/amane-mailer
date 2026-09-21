using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Amane.Mailer.Setup;
using Microsoft.AspNetCore.Antiforgery;

namespace Amane.Mailer.Admin;

/// <summary>
/// Instance-owner view of the product-managed ACS and Google secrets.
/// This is deliberately not a general secret-management surface.
/// </summary>
public static class AdminSecretsPage
{
    public const string PagePath = "/admin/secrets";
    public const string AcsRotationPath = "/admin/secrets/acs";

    private const string AcsSecretLabel = "ACS provider connection string";
    private const string AuthorityManaged = "managed instance";
    private const string AuthorityCompatibility = "operator-owned compatibility / legacy/manual";
    private const string Configured = "設定済み";
    private const string NotConfigured = "未設定・無効";
    private const string RuntimeUnavailable = "unavailable";
    private const string RuntimeApplied = "反映済み";
    private const string RuntimeRestartPending = "再起動待ち";
    private const string BackupIncluded = "full-instance backup対象";
    private const string BackupOperatorManaged = "operator-managed / full-instance backup対象外";

    public static async Task<IResult> RenderAsync(
        HttpContext context,
        AdminUserRepository userRepository,
        AdminDeadLetterCountCache deadLetterCountCache,
        MailRequestRepository mailRequestRepository,
        InstanceConfigurationRepository instanceConfigurationRepository,
        MailerOptions mailerOptions,
        AdminGoogleOptions googleOptions,
        IConfiguration configuration,
        IAntiforgery antiforgery,
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

        var managedAcs = IsManagedAcs(instanceConfiguration);
        var acsPath = managedAcs ? instanceConfiguration!.ProviderSecretRef : null;
        var currentAcsSecret = string.Empty;
        var acsConfigured = managedAcs
            && FirstRunSetupStorage.TryReadValidAcsSecret(acsPath!, out currentAcsSecret);
        currentAcsSecret = acsConfigured ? currentAcsSecret : string.Empty;
        var acsRuntime = acsConfigured
            ? ResolveAcsRuntimeDisplay(mailerOptions.AcsConnectionString, currentAcsSecret)
            : RuntimeUnavailable;
        var acsUpdated = acsConfigured ? GetAcsFileUpdatedUtc(acsPath) : null;

        var googleStatus = AdminGoogleSettingsStatus.Evaluate(
            instanceConfiguration,
            googleOptions,
            configuration);
        var googleSecretPath = googleStatus.SavedUsesManaged
            ? instanceConfiguration?.GoogleClientSecretRef
            : null;
        var googleConfigured = googleStatus.SavedUsesManaged
            ? AdminGoogleSecretStore.IsSecretConfigured(googleSecretPath)
            : !string.IsNullOrWhiteSpace(configuration[AdminGoogleOptions.ClientSecretKey]);
        var googleUpdated = googleConfigured && googleStatus.SavedUsesManaged
            ? GetGoogleFileUpdatedUtc(googleSecretPath)
            : null;

        var flash = context.Request.Query["saved"].ToString() is "1"
            ? acsRuntime == RuntimeRestartPending
                ? "ACS secret を保存しました。反映には Mailer container / process の再起動が必要です。"
                : "ACS secret を保存しました。"
            : null;

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        return Results.Content(
            RenderHtml(
                access,
                deadLetterCount,
                instanceConfiguration,
                managedAcs,
                acsConfigured,
                acsRuntime,
                ResolveAcsBackupDisplay(acsPath, managedAcs),
                FormatUpdatedUtc(acsUpdated),
                googleStatus,
                googleConfigured,
                ResolveGoogleBackupDisplay(googleSecretPath, googleStatus.SavedUsesManaged),
                FormatUpdatedUtc(googleUpdated),
                csrfToken,
                flash),
            "text/html; charset=utf-8");
    }

    public static async Task<IResult> RotateAcsAsync(
        HttpContext context,
        InstanceConfigurationRepository instanceConfigurationRepository,
        AdminUserRepository userRepository,
        AdminAuditRepository auditRepository,
        MailerAdminOptions adminOptions,
        IAntiforgery antiforgery,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
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
        if (!IsManagedAcs(current))
            return Results.Conflict();

        var replacement = form["acs_connection_string"].ToString().Trim();
        if (!FirstRunSetupStorage.IsValidAcsConnectionString(replacement))
        {
            await WriteRotationAuditAsync(
                context,
                auditRepository,
                adminOptions,
                loggerFactory,
                timeProvider,
                AdminAuditLog.Results.Failure,
                AdminAuditLog.ErrorCodes.OperationFailed,
                cancellationToken);
            return Results.BadRequest("Invalid ACS connection string.");
        }

        try
        {
            if (!FirstRunSetupStorage.TryReplaceAcsSecret(current!.ProviderSecretRef!, replacement))
            {
                await WriteRotationAuditAsync(
                    context,
                    auditRepository,
                    adminOptions,
                    loggerFactory,
                    timeProvider,
                    AdminAuditLog.Results.Failure,
                    AdminAuditLog.ErrorCodes.OperationFailed,
                    cancellationToken);
                return Results.Conflict();
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException
            or SecurityException
            or SecretOperationException
            or SecureFileWriteException)
        {
            await WriteRotationAuditAsync(
                context,
                auditRepository,
                adminOptions,
                loggerFactory,
                timeProvider,
                AdminAuditLog.Results.Failure,
                AdminAuditLog.ErrorCodes.OperationFailed,
                cancellationToken);
            return Results.Conflict();
        }

        await WriteRotationAuditAsync(
            context,
            auditRepository,
            adminOptions,
            loggerFactory,
            timeProvider,
            AdminAuditLog.Results.Success,
            errorCode: null,
            cancellationToken);
        return new SeeOtherRedirectResult($"{PagePath}?saved=1");
    }

    internal static bool IsManagedAcs(InstanceConfigurationRow? instanceConfiguration) =>
        instanceConfiguration?.InitializedAt is not null
        && string.Equals(instanceConfiguration.ProviderType, "acs", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(instanceConfiguration.ProviderSecretRef);

    internal static string ResolveAcsRuntimeDisplay(string startupSecret, string currentSecret)
    {
        if (string.IsNullOrWhiteSpace(startupSecret) || string.IsNullOrWhiteSpace(currentSecret))
            return RuntimeUnavailable;

        return SecretsMatch(startupSecret, currentSecret)
            ? RuntimeApplied
            : RuntimeRestartPending;
    }

    internal static bool SecretsMatch(string startupSecret, string currentSecret)
    {
        var startupBytes = Encoding.UTF8.GetBytes(startupSecret.Trim());
        var currentBytes = Encoding.UTF8.GetBytes(currentSecret.Trim());
        var startupDigest = SHA256.HashData(startupBytes);
        var currentDigest = SHA256.HashData(currentBytes);
        try
        {
            return CryptographicOperations.FixedTimeEquals(startupDigest, currentDigest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(startupBytes);
            CryptographicOperations.ZeroMemory(currentBytes);
            CryptographicOperations.ZeroMemory(startupDigest);
            CryptographicOperations.ZeroMemory(currentDigest);
        }
    }

    internal static string ResolveAcsBackupDisplay(string? secretPath, bool managedAcs) =>
        managedAcs && IsCanonicalPath(secretPath, FirstRunSetupConstants.DefaultAcsSecretPath)
            ? BackupIncluded
            : BackupOperatorManaged;

    internal static string ResolveGoogleBackupDisplay(string? secretPath, bool managedGoogle) =>
        managedGoogle && IsCanonicalPath(secretPath, AdminGoogleSecretStore.DefaultSecretPath)
            ? BackupIncluded
            : BackupOperatorManaged;

    internal static bool IsCanonicalPath(string? path, string canonicalPath)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(canonicalPath),
                comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static string RenderHtml(
        AdminTenantAccess access,
        int deadLetterCount,
        InstanceConfigurationRow? instanceConfiguration,
        bool managedAcs,
        bool acsConfigured,
        string acsRuntime,
        string acsBackup,
        string acsUpdated,
        AdminGoogleSettingsStatus googleStatus,
        bool googleConfigured,
        string googleBackup,
        string googleUpdated,
        string csrfToken,
        string? flash)
    {
        var html = new StringBuilder();
        AdminLayout.AppendDocumentStart(
            html,
            "Secret管理 - Amane Admin",
            AdminNavItem.Secrets,
            deadLetterCount,
            access);

        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Secret管理\">");
        html.AppendLine("                  <h1 class=\"ops-heading\">Secret管理</h1>");
        html.AppendLine("                  <p class=\"ops-description\">Product-managed secret の設定状態と管理場所を確認します。secret 値や保存 path は表示しません。</p>");
        if (flash is not null)
        {
            html.Append("                  <p class=\"ops-meta\" role=\"status\">");
            html.Append(Html(flash));
            html.AppendLine("</p>");
        }

        html.AppendLine("                </section>");
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"ACS Provider Secret\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">ACS Provider Secret</h2>");
        html.AppendLine("                  <dl class=\"ops-dl\">");
        AppendTrustedDefinition(html, "Secret", AcsSecretLabel);
        AppendTrustedDefinition(html, "Configured", acsConfigured ? Configured : NotConfigured);
        AppendTrustedDefinition(html, "Authority", managedAcs ? AuthorityManaged : AuthorityCompatibility);
        AppendTrustedDefinition(html, "Runtime", acsRuntime);
        AppendTrustedDefinition(html, "Backup", acsBackup);
        AppendTrustedDefinition(html, "File updated", acsUpdated);
        html.AppendLine("                  </dl>");

        if (IsManagedAcs(instanceConfiguration))
        {
            html.AppendLine("                  <form method=\"post\" action=\"/admin/secrets/acs\" class=\"stack-form\">");
            html.Append("                    <input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"");
            html.Append(Html(csrfToken));
            html.AppendLine("\">");
            html.AppendLine("                    <label>");
            html.AppendLine("                      <span>新しい ACS connection string</span>");
            html.AppendLine("                      <input name=\"acs_connection_string\" type=\"password\" autocomplete=\"new-password\" required>");
            html.AppendLine("                    </label>");
            html.AppendLine("                    <label>");
            html.AppendLine("                      <input type=\"checkbox\" name=\"confirmation\" value=\"confirm\" required> ACS secret を置き換えることを確認します");
            html.AppendLine("                    </label>");
            html.AppendLine("                    <button type=\"submit\">ACS secret をローテーション</button>");
            html.AppendLine("                  </form>");
            html.AppendLine("                  <p class=\"ops-meta\">保存した secret は再表示されません。新しい値は次回の Mailer container / process 起動時に反映されます。</p>");
        }
        else
        {
            html.AppendLine("                  <p class=\"ops-meta\">managed-v2 ACS の初期化が完了していないため、ここでは rotation できません。</p>");
        }

        html.AppendLine("                </section>");
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Google Client Secret\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">Google Client Secret</h2>");
        html.AppendLine("                  <dl class=\"ops-dl\">");
        AppendTrustedDefinition(html, "Configured", googleConfigured ? Configured : NotConfigured);
        AppendTrustedDefinition(html, "Authority", googleStatus.SavedAuthorityDisplay);
        AppendTrustedDefinition(html, "Runtime", googleStatus.ReflectionDisplay);
        AppendTrustedDefinition(html, "Backup", googleBackup);
        AppendTrustedDefinition(html, "File updated", googleUpdated);
        html.AppendLine("                  </dl>");
        html.AppendLine("                  <p class=\"ops-meta\"><a href=\"/admin/auth-settings\">Google Client Secret の設定・rotation（認証設定）</a></p>");
        html.AppendLine("                </section>");

        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Operator-managed secrets\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">Operator-managed secrets</h2>");
        html.AppendLine("                  <p class=\"ops-description\">次の credential は Operator-managed です。この画面では状態確認・編集・保存をしません。</p>");
        html.AppendLine("                  <ul>");
        html.AppendLine("                    <li>Bounce Queue credential</li>");
        html.AppendLine("                    <li>backup / rclone credential</li>");
        html.AppendLine("                    <li>age identity</li>");
        html.AppendLine("                    <li>legacy / manual secret</li>");
        html.AppendLine("                  </ul>");
        html.AppendLine("                  <p class=\"ops-meta\">Admin password、Mailer API Key、bootstrap token、session、service token、metrics bearer token はそれぞれ専用の管理契約を維持します。</p>");
        html.AppendLine("                </section>");
        AdminLayout.AppendDocumentEnd(html);
        return html.ToString();
    }

    private static async Task WriteRotationAuditAsync(
        HttpContext context,
        AdminAuditRepository auditRepository,
        MailerAdminOptions adminOptions,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        string result,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        await AdminAuditLog.WriteBestEffortAsync(
            auditRepository,
            loggerFactory.CreateLogger(AdminAuditLog.LoggerCategory),
            new AdminAuditEvent
            {
                EventType = AdminAuditLog.EventTypes.InstanceProviderSecretRotated,
                Actor = AdminAuditLog.ResolveActor(context),
                OccurredAt = timeProvider.GetUtcNow(),
                SourceIp = adminOptions.ResolveAuditSourceIp(AdminAuditLog.ResolveSourceIp(context)),
                UserAgentSummary = AdminAuditLog.SummarizeUserAgent(context),
                TargetType = AdminAuditLog.TargetTypes.InstanceConfiguration,
                TargetId = "1",
                Result = result,
                ErrorCode = errorCode,
                FieldName = "acs_provider_secret",
            },
            cancellationToken);
    }

    private static DateTimeOffset? GetAcsFileUpdatedUtc(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !FirstRunSetupStorage.TryReadValidAcsSecret(path, out _))
            return null;

        return GetSafeFileUpdatedUtc(path);
    }

    private static DateTimeOffset? GetGoogleFileUpdatedUtc(string? path)
    {
        if (!AdminGoogleSecretStore.IsSecretConfigured(path))
            return null;

        return GetSafeFileUpdatedUtc(path!);
    }

    private static DateTimeOffset? GetSafeFileUpdatedUtc(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            FileSystemSafetyGuard.EnsureTargetFileIsSafeIfExists(fullPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory) || !File.Exists(fullPath))
                return null;

            FileSystemSafetyGuard.EnsureDirectoryIsSafe(directory);
            if (!new HostSetupFileSystem().IsOwnerOnlyFile(fullPath))
                return null;

            return new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath));
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException
            or SecurityException
            or SecretOperationException)
        {
            return null;
        }
    }

    private static string FormatUpdatedUtc(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "n/a";

    /// <summary>
    /// Appends application-controlled display text as UTF-8. Callers must never pass user input.
    /// </summary>
    private static void AppendTrustedDefinition(StringBuilder html, string term, string value)
    {
        html.Append("                    <dt>");
        html.Append(term);
        html.AppendLine("</dt>");
        html.Append("                    <dd>");
        html.Append(value);
        html.AppendLine("</dd>");
    }

    private static string Html(string value) => HtmlEncoder.Default.Encode(value);

    private static bool HasConfirmation(IFormCollection form) =>
        string.Equals(form["confirmation"].ToString(), "confirm", StringComparison.Ordinal);

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
