namespace Amane.Mailer.Admin;

/// <summary>
/// Maps Admin HTTP endpoints. Middleware (auth, local-address, static files) stays in
/// <see cref="MailerAdminExtensions.MapAdminIfEnabled"/>.
/// </summary>
internal static class AdminEndpointMapping
{
    internal static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/admin", RedirectAdminHome).AllowAnonymous();
        app.MapGet("/admin/login", AdminAuthenticationHandlers.RenderLoginPage).AllowAnonymous();
        app.MapGet("/admin/mail-requests", AdminMailRequestsPage.RenderAsync).RequireAuthorization();
        app.MapGet("/admin/senders", AdminSendersPage.RenderAsync).RequireAuthorization();
        app.MapGet("/admin/senders/{senderId:guid}", AdminSendersPage.RenderDetailAsync).RequireAuthorization();
        app.MapGet("/admin/dead-letters", AdminDeadLettersPage.RenderAsync).RequireAuthorization();
        app.MapGet("/admin/webhook-dead-letters", AdminWebhookDeadLettersPage.RenderAsync).RequireAuthorization();
        app.MapGet("/admin/suppressions", AdminSuppressionsPage.RenderAsync).RequireAuthorization();
        app.MapGet("/admin/audit-log", AdminAuditLogPage.RenderAsync).RequireAuthorization();
        app.MapGet("/admin/audit-log/{id:long}", AdminAuditLogDetailPage.RenderAsync).RequireAuthorization();
        app.MapGet("/admin/ops", AdminOpsPage.RenderAsync).RequireAuthorization();
        app.MapGet("/admin/setup-status", AdminSetupStatusPage.RenderAsync).RequireAuthorization();
        app.MapPost("/admin/ops/checkpoint", AdminDbOpsHandlers.CheckpointAsync).RequireAuthorization();
        app.MapPost("/admin/ops/backup", AdminDbOpsHandlers.BackupAsync).RequireAuthorization();
        app.MapPost("/admin/ops/live-sending", AdminOpsPage.SetLiveSendingAsync).RequireAuthorization();
        app.MapPost("/admin/senders", AdminSenderMutationHandlers.CreateAsync).RequireAuthorization();
        app.MapPost("/admin/senders/{senderId:guid}/enable", AdminSenderMutationHandlers.EnableAsync).RequireAuthorization();
        app.MapPost("/admin/senders/{senderId:guid}/disable", AdminSenderMutationHandlers.DisableAsync).RequireAuthorization();
        app.MapPost("/admin/senders/{senderId:guid}/api-keys", AdminSenderMutationHandlers.CreateApiKeyAsync).RequireAuthorization();
        app.MapPost("/admin/senders/{senderId:guid}/api-keys/{keyId:guid}/revoke", AdminSenderMutationHandlers.RevokeApiKeyAsync).RequireAuthorization();
        app.MapGet("/admin/mail-requests/{id}", AdminMailRequestDetailPage.RenderAsync).RequireAuthorization();
        app.MapGet(
                "/admin/mail-requests/{id}/recipients/bcc/{ordinal:int}",
                AdminBccRecipientRevealPage.RenderAsync)
            .RequireAuthorization();
        app.MapGet("/admin/mail-requests/{id}/body", AdminMailRequestBodyPage.RenderAsync).RequireAuthorization();
        app.MapGet(
                "/admin/mail-requests/{id}/attachments/{order:int}/filename",
                AdminMailRequestAttachmentFilenamePage.RenderAsync)
            .RequireAuthorization();
        app.MapPost("/admin/mail-requests/{id}/retry", AdminMailRequestMutationHandlers.RetryAsync).RequireAuthorization();
        app.MapPost("/admin/mail-requests/{id}/cancel", AdminMailRequestMutationHandlers.CancelAsync).RequireAuthorization();
        app.MapPost("/admin/api/login", AdminAuthenticationHandlers.LoginAsync).AllowAnonymous();
        app.MapPost("/admin/api/logout", AdminAuthenticationHandlers.LogoutAsync).RequireAuthorization();
    }

    private static IResult RedirectAdminHome(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true
            ? Results.Redirect("/admin/mail-requests")
            : Results.Redirect("/admin/login");
}
