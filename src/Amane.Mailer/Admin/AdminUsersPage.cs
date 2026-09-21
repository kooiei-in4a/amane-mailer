using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.AspNetCore.Antiforgery;

namespace Amane.Mailer.Admin;

public static class AdminUsersPage
{
    public const string PagePath = "/admin/users";
    public const string EnablePath = "/admin/users/{id:long}/enable";
    public const string DisablePath = "/admin/users/{id:long}/disable";
    public const string GoogleUnlinkPath = "/admin/users/{id:long}/google-unlink";

    public static async Task<IResult> RenderAsync(
        HttpContext context,
        AdminUserRepository userRepository,
        AdminDeadLetterCountCache deadLetterCountCache,
        MailRequestRepository mailRequestRepository,
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
        var users = await userRepository.ListUserSummariesAsync(cancellationToken);
        var csrfToken = HtmlEncoder.Default.Encode(
            antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty);
        var flash = ResolveFlash(context.Request.Query["notice"].ToString());

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        return Results.Content(
            RenderHtml(access, deadLetterCount, users, csrfToken, flash),
            "text/html; charset=utf-8");
    }

    public static Task<IResult> EnableAsync(
        long id,
        HttpContext context,
        AdminUserRepository userRepository,
        AdminAuditRepository auditRepository,
        MailerAdminOptions options,
        IAntiforgery antiforgery,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        SetEnabledAsync(
            id,
            enabled: true,
            context,
            userRepository,
            auditRepository,
            options,
            antiforgery,
            loggerFactory,
            timeProvider,
            cancellationToken);

    public static Task<IResult> DisableAsync(
        long id,
        HttpContext context,
        AdminUserRepository userRepository,
        AdminAuditRepository auditRepository,
        MailerAdminOptions options,
        IAntiforgery antiforgery,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        SetEnabledAsync(
            id,
            enabled: false,
            context,
            userRepository,
            auditRepository,
            options,
            antiforgery,
            loggerFactory,
            timeProvider,
            cancellationToken);

    public static async Task<IResult> UnlinkGoogleAsync(
        long id,
        HttpContext context,
        AdminUserRepository userRepository,
        AdminGoogleIdentityRepository googleIdentityRepository,
        AdminAuditRepository auditRepository,
        MailerAdminOptions options,
        IAntiforgery antiforgery,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var gate = await ValidateMutationAsync(context, antiforgery, userRepository, cancellationToken);
        if (gate is not null)
            return gate;

        var form = await ReadFormAsync(context, cancellationToken);
        if (form is null || !HasConfirmation(form))
            return Results.BadRequest("Explicit confirmation is required.");

        var user = await userRepository.GetUserByIdAsync(id, cancellationToken);
        if (user is null)
            return Results.NotFound();

        var result = await googleIdentityRepository.TryUnlinkByUsernameAsync(
            user.Username,
            timeProvider.GetUtcNow(),
            AdminSessionRevokeReasons.GoogleIdentityUnlinked,
            cancellationToken);

        switch (result)
        {
            case AdminGoogleUnlinkResult.UserNotFound:
            case AdminGoogleUnlinkResult.MappingNotFound:
                return Results.NotFound();
            case AdminGoogleUnlinkResult.Unlinked:
                await WriteAuditAsync(
                    context,
                    auditRepository,
                    options,
                    loggerFactory,
                    timeProvider,
                    AdminAuditLog.EventTypes.GoogleIdentityUnlinked,
                    user.Username,
                    cancellationToken);
                return SeeOther($"{PagePath}?notice=google_unlinked");
            default:
                return Results.Conflict();
        }
    }

    private static async Task<IResult> SetEnabledAsync(
        long id,
        bool enabled,
        HttpContext context,
        AdminUserRepository userRepository,
        AdminAuditRepository auditRepository,
        MailerAdminOptions options,
        IAntiforgery antiforgery,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var gate = await ValidateMutationAsync(context, antiforgery, userRepository, cancellationToken);
        if (gate is not null)
            return gate;

        var form = await ReadFormAsync(context, cancellationToken);
        if (form is null || !HasConfirmation(form))
            return Results.BadRequest("Explicit confirmation is required.");

        var result = await userRepository.SetEnabledAsync(id, enabled, cancellationToken);
        switch (result)
        {
            case AdminUserEnabledMutationResult.NotFound:
                return Results.NotFound();
            case AdminUserEnabledMutationResult.InstanceOwnerProtected:
                return SeeOther($"{PagePath}?notice=owner_protected");
            case AdminUserEnabledMutationResult.Unchanged:
                return SeeOther(PagePath);
            case AdminUserEnabledMutationResult.Changed:
                var user = await userRepository.GetUserByIdAsync(id, cancellationToken);
                if (user is not null)
                {
                    await WriteAuditAsync(
                        context,
                        auditRepository,
                        options,
                        loggerFactory,
                        timeProvider,
                        enabled
                            ? AdminAuditLog.EventTypes.AdminUserEnabled
                            : AdminAuditLog.EventTypes.AdminUserDisabled,
                        user.Username,
                        cancellationToken);
                }

                return SeeOther(enabled
                    ? $"{PagePath}?notice=enabled"
                    : $"{PagePath}?notice=disabled");
            default:
                return Results.Conflict();
        }
    }

    private static async Task<IResult?> ValidateMutationAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        AdminUserRepository userRepository,
        CancellationToken cancellationToken)
    {
        if (!await ValidateAntiforgeryAsync(context, antiforgery))
            return Results.BadRequest("Invalid CSRF token.");

        var accessResult = await AdminManagedConfigurationAuthorization.RequireInstanceOwnerAsync(
            context,
            userRepository,
            cancellationToken);
        return accessResult.Error;
    }

    private static async Task WriteAuditAsync(
        HttpContext context,
        AdminAuditRepository auditRepository,
        MailerAdminOptions options,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        string eventType,
        string username,
        CancellationToken cancellationToken)
    {
        await AdminAuditLog.WriteBestEffortAsync(
            auditRepository,
            loggerFactory.CreateLogger(AdminAuditLog.LoggerCategory),
            AdminAuditLog.SanitizeForOutput(new AdminAuditEvent
            {
                EventType = eventType,
                Actor = AdminAuditLog.ResolveActor(context),
                OccurredAt = timeProvider.GetUtcNow(),
                SourceIp = options.ResolveAuditSourceIp(AdminAuditLog.ResolveSourceIp(context)),
                UserAgentSummary = AdminAuditLog.SummarizeUserAgent(context),
                TargetType = AdminAuditLog.TargetTypes.AdminUser,
                TargetId = username,
                Result = AdminAuditLog.Results.Success,
            }),
            cancellationToken);
    }

    private static async Task<IFormCollection?> ReadFormAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await context.Request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static bool HasConfirmation(IFormCollection form) =>
        string.Equals(form["confirmation"].ToString(), "confirm", StringComparison.Ordinal)
        || string.Equals(form["confirm"].ToString(), "on", StringComparison.Ordinal)
        || string.Equals(form["confirm"].ToString(), "true", StringComparison.OrdinalIgnoreCase);

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

    private static IResult SeeOther(string url) => new SeeOtherRedirectResult(url);

    private static string? ResolveFlash(string notice) =>
        notice switch
        {
            "enabled" => "管理者アカウントを有効化しました。",
            "disabled" => "管理者アカウントを無効化しました。",
            "google_unlinked" => "Google identity の紐付けを解除しました。",
            "owner_protected" => "Instance Owner は無効化できません。",
            _ => null,
        };

    internal static string RenderHtml(
        AdminTenantAccess access,
        int deadLetterCount,
        IReadOnlyList<AdminUserSummary> users,
        string csrfToken,
        string? flash)
    {
        var html = new StringBuilder();
        AdminLayout.AppendDocumentStart(html, "管理者 — Amane Mailer Admin", AdminNavItem.AdminUsers, deadLetterCount, access);
        html.AppendLine("      <section class=\"admin-card\">");
        html.AppendLine("        <div class=\"admin-card-header\">");
        html.AppendLine("          <h1>管理者</h1>");
        html.AppendLine("          <p>Instance Owner 向けの管理者アカウント一覧です。Password / Google subject / secret は表示しません。</p>");
        html.AppendLine("        </div>");
        if (flash is not null)
        {
            html.Append("        <p class=\"admin-flash\" role=\"status\">");
            html.Append(Html(flash));
            html.AppendLine("</p>");
        }

        html.AppendLine("        <div class=\"admin-table-wrap\">");
        html.AppendLine("          <table class=\"admin-table\">");
        html.AppendLine("            <thead>");
        html.AppendLine("              <tr>");
        html.AppendLine("                <th>Username</th>");
        html.AppendLine("                <th>状態</th>");
        html.AppendLine("                <th>属性</th>");
        html.AppendLine("                <th>Tenant scopes</th>");
        html.AppendLine("                <th>Google</th>");
        html.AppendLine("                <th>操作</th>");
        html.AppendLine("              </tr>");
        html.AppendLine("            </thead>");
        html.AppendLine("            <tbody>");
        foreach (var user in users)
            AppendUserRow(html, user, csrfToken);

        html.AppendLine("            </tbody>");
        html.AppendLine("          </table>");
        html.AppendLine("        </div>");
        html.AppendLine("      </section>");
        AdminLayout.AppendDocumentEnd(html);
        return html.ToString();
    }

    private static void AppendUserRow(StringBuilder html, AdminUserSummary user, string csrfToken)
    {
        html.AppendLine("              <tr>");
        html.Append("                <td><code>");
        html.Append(Html(user.Username));
        html.AppendLine("</code></td>");
        html.Append("                <td><span class=\"");
        html.Append(user.Disabled ? "sender-status-disabled" : "sender-status-enabled");
        html.Append("\">");
        html.Append(user.Disabled ? "disabled" : "enabled");
        html.AppendLine("</span></td>");
        html.Append("                <td>");
        html.Append(Html(FormatAttributes(user)));
        html.AppendLine("</td>");
        html.Append("                <td>");
        html.Append(Html(FormatScopes(user)));
        html.AppendLine("</td>");
        html.Append("                <td>");
        html.Append(user.GoogleLinked ? "Google linked" : "Google not linked");
        html.AppendLine("</td>");
        html.AppendLine("                <td class=\"admin-actions\">");
        if (user.IsInstanceOwner)
        {
            html.AppendLine("                  <span class=\"muted\">Instance Owner は無効化不可</span>");
        }
        else if (user.Disabled)
        {
            AppendMutationForm(
                html,
                user.Id,
                "enable",
                "有効化",
                "この管理者を有効化することを確認します。",
                csrfToken);
        }
        else
        {
            AppendMutationForm(
                html,
                user.Id,
                "disable",
                "無効化",
                "この管理者を無効化することを確認します。",
                csrfToken);
        }

        if (user.GoogleLinked)
        {
            AppendMutationForm(
                html,
                user.Id,
                "google-unlink",
                "Google unlink",
                "Google identity の紐付けを解除することを確認します。",
                csrfToken);
        }

        html.AppendLine("                </td>");
        html.AppendLine("              </tr>");
    }

    private static void AppendMutationForm(
        StringBuilder html,
        long userId,
        string operation,
        string buttonLabel,
        string confirmationLabel,
        string csrfToken)
    {
        html.Append("                  <form method=\"post\" action=\"/admin/users/");
        html.Append(userId.ToString(CultureInfo.InvariantCulture));
        html.Append('/');
        html.Append(operation);
        html.AppendLine("\" class=\"ops-form\">");
        html.Append("                    <input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"");
        html.Append(csrfToken);
        html.AppendLine("\">");
        html.Append("                    <label><input type=\"checkbox\" name=\"confirmation\" value=\"confirm\" required> ");
        html.Append(Html(confirmationLabel));
        html.AppendLine("</label>");
        html.Append("                    <button type=\"submit\">");
        html.Append(Html(buttonLabel));
        html.AppendLine("</button>");
        html.AppendLine("                  </form>");
    }

    private static string FormatAttributes(AdminUserSummary user)
    {
        var parts = new List<string>(3);
        if (user.IsInstanceOwner)
            parts.Add("Instance Owner");
        if (user.IsBreakGlass)
            parts.Add("break-glass admin");
        if (!user.IsInstanceOwner && !user.IsBreakGlass)
            parts.Add("tenant-scoped admin");
        return string.Join(", ", parts);
    }

    private static string FormatScopes(AdminUserSummary user)
    {
        if (user.IsInstanceOwner || user.IsBreakGlass)
            return "—";
        if (user.TenantScopes.Count == 0)
            return "(none)";
        return string.Join(", ", user.TenantScopes.Select(static id => id.ToString("D")));
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
