using Amane.Mailer.Configuration;
using Amane.Mailer.Operations.AcsSetup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// Routes for the assistant. Every state transition is a POST that redirects back to
/// <c>GET /</c>, so a refresh or a browser back never resubmits a step and never re-renders
/// operator-entered secrets or addresses.
/// </summary>
internal static class SetupAssistantEndpoints
{
    internal static void MapSetupAssistant(
        this IEndpointRouteBuilder routes,
        SetupAssistantSessionManager sessions,
        ISetupAssistantOperations operations)
    {
        routes.MapGet("/", context => RenderCurrentAsync(context, sessions));
        routes.MapGet(SetupAssistantPages.StyleSheetPath, static context =>
        {
            context.Response.ContentType = "text/css; charset=utf-8";
            return context.Response.WriteAsync(SetupAssistantPages.StyleSheet);
        });

        routes.MapPost("/token", context => RedeemTokenAsync(context, sessions));
        routes.MapPost("/welcome", context => StepAsync(context, sessions, operations, Sync(Welcome)));
        routes.MapPost("/preflight", context => StepAsync(context, sessions, operations, PreflightAsync));
        routes.MapPost("/mode", context => StepAsync(context, sessions, operations, Sync(SelectMode)));
        routes.MapPost("/tenant", context => StepAsync(context, sessions, operations, Sync(TenantBasics)));
        routes.MapPost("/provider", context => StepAsync(context, sessions, operations, Sync(ProviderSettings)));
        routes.MapPost("/acs", context => StepAsync(context, sessions, operations, Sync(AcsSettings)));
        routes.MapPost("/confirm", context => StepAsync(context, sessions, operations, ConfirmAsync));
        routes.MapPost("/verify", context => StepAsync(context, sessions, operations, VerifyAsync));
        routes.MapPost("/admin-choice", context => StepAsync(context, sessions, operations, Sync(AdminChoice)));
        routes.MapPost("/admin-preflight", context => StepAsync(context, sessions, operations, AdminPreflightAsync));
        routes.MapPost("/admin-bootstrap", context => StepAsync(context, sessions, operations, AdminBootstrapAsync));
        routes.MapPost("/finish", context => StepAsync(context, sessions, operations, Sync(Finish)));
        routes.MapPost("/cancel", context => CancelAsync(context, sessions));
    }

    private static Func<SetupAssistantStepContext, Task> Sync(Action<SetupAssistantStepContext> handler) =>
        step =>
        {
            handler(step);
            return Task.CompletedTask;
        };

    private static Task RenderCurrentAsync(HttpContext context, SetupAssistantSessionManager sessions)
    {
        var session = sessions.TryResolve(SetupAssistantSecurity.ReadSessionCookie(context.Request));
        if (session is null)
        {
            SetupAssistantSecurity.ClearSessionCookie(context.Response);
            return WriteHtmlAsync(context, SetupAssistantPages.RenderLanding(null));
        }

        return WriteHtmlAsync(context, SetupAssistantPages.Render(session));
    }

    private static async Task RedeemTokenAsync(
        HttpContext context,
        SetupAssistantSessionManager sessions)
    {
        var form = await ReadFormAsync(context);
        var exchange = sessions.TryRedeem(form?["one_time_token"].ToString(), out var session);
        if (exchange != SetupAssistantTokenExchange.Redeemed || session is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await WriteHtmlAsync(
                context,
                SetupAssistantPages.RenderLanding(DescribeExchange(exchange)));
            return;
        }

        SetupAssistantSecurity.WriteSessionCookie(context.Response, session);
        Redirect(context);
    }

    private static async Task CancelAsync(HttpContext context, SetupAssistantSessionManager sessions)
    {
        var session = sessions.TryResolve(SetupAssistantSecurity.ReadSessionCookie(context.Request));
        if (session is null || !await ValidateCsrfAsync(context, session))
        {
            return;
        }

        SetupAssistantSecurity.ClearSessionCookie(context.Response);
        await WriteHtmlAsync(
            context,
            SetupAssistantPages.RenderTerminated(
                "Assistant を中止しました。ローカルサーバーを停止します。設定は変更されていません。"));
        sessions.Stop(SetupAssistantShutdownReason.Cancelled);
    }

    private static async Task StepAsync(
        HttpContext context,
        SetupAssistantSessionManager sessions,
        ISetupAssistantOperations operations,
        Func<SetupAssistantStepContext, Task> handler)
    {
        var session = sessions.TryResolve(SetupAssistantSecurity.ReadSessionCookie(context.Request));
        if (session is null)
        {
            SetupAssistantSecurity.ClearSessionCookie(context.Response);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await WriteHtmlAsync(
                context,
                SetupAssistantPages.RenderLanding("session が無効になりました。再度トークンを入力してください。"));
            return;
        }

        var form = await ReadFormAsync(context);
        if (form is null || !SetupAssistantSecurity.ValidateCsrf(session, form))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteHtmlAsync(
                context,
                SetupAssistantPages.RenderTerminated("要求を検証できませんでした。画面を開き直してください。"));
            return;
        }

        await handler(new SetupAssistantStepContext(session, form, sessions, operations, context.RequestAborted));

        // A step may finish the run or hit a deadline. Redirecting would send the browser to a
        // server that is already stopping, so the farewell page is written on this response.
        if (sessions.ShutdownToken.IsCancellationRequested)
        {
            SetupAssistantSecurity.ClearSessionCookie(context.Response);
            await WriteHtmlAsync(
                context,
                SetupAssistantPages.RenderTerminated(DescribeShutdown(sessions.ShutdownReason)));
            return;
        }

        Redirect(context);
    }

    private static string DescribeShutdown(SetupAssistantShutdownReason reason) => reason switch
    {
        SetupAssistantShutdownReason.Completed =>
            "Easy Setup を終了しました。ローカルサーバーは停止済みです。このタブは閉じてかまいません。",
        SetupAssistantShutdownReason.IdleTimeout =>
            "操作がないまま時間が経過したため、session を破棄してローカルサーバーを停止しました。",
        SetupAssistantShutdownReason.AbsoluteTimeout =>
            "session の上限時間に達したため、session を破棄してローカルサーバーを停止しました。",
        _ => "Assistant を終了しました。ローカルサーバーは停止済みです。",
    };

    private static void Welcome(SetupAssistantStepContext step) =>
        step.Session.MoveTo(SetupAssistantStep.DockerPreflight);

    private static async Task PreflightAsync(SetupAssistantStepContext step)
    {
        if (step.Action == "continue")
        {
            if (step.Session.DockerPreflight is { Passed: true })
            {
                step.Session.MoveTo(SetupAssistantStep.ModeSelection);
            }
            else
            {
                step.Session.Reject(SetupAssistantRejection.StepNotAvailable);
            }

            return;
        }

        step.Session.SetDockerPreflight(await step.Operations.CheckDockerAsync(step.CancellationToken));
        step.Session.MoveTo(SetupAssistantStep.DockerPreflight);
    }

    private static void SelectMode(SetupAssistantStepContext step)
    {
        var raw = step.Field("mode");
        if (string.Equals(raw, SetupAssistantInputs.ManualModeValue, StringComparison.Ordinal))
        {
            step.Session.MoveTo(SetupAssistantStep.ManualModeGuidance);
            return;
        }

        if (step.Action == "back")
        {
            step.Session.MoveTo(SetupAssistantStep.ModeSelection);
            return;
        }

        if (!SetupAssistantInputs.TryParseAutomatableMode(raw, out var mode))
        {
            step.Session.Reject(SetupAssistantRejection.ModeNotSelectable);
            return;
        }

        step.Session.SetMode(mode);
        step.Session.MoveTo(SetupAssistantStep.TenantBasics);
    }

    private static void TenantBasics(SetupAssistantStepContext step)
    {
        var tenantName = step.Field("tenant_name");
        var sourceService = step.Field("source_service");
        var senderEmail = step.Field("sender_email");
        var senderDisplayName = step.Field("sender_display_name");

        if (!SetupAssistantInputs.IsIdentifier(tenantName)
            || !SetupAssistantInputs.IsSourceService(sourceService))
        {
            step.Session.Reject(SetupAssistantRejection.InvalidIdentifier);
            return;
        }

        if (!SetupAssistantInputs.IsEmail(senderEmail))
        {
            step.Session.Reject(SetupAssistantRejection.InvalidEmail);
            return;
        }

        if (!SetupAssistantInputs.IsDisplayText(senderDisplayName))
        {
            step.Session.Reject(SetupAssistantRejection.MissingRequiredField);
            return;
        }

        step.Session.SetTenantBasics(tenantName, sourceService, senderEmail, senderDisplayName);
        step.Session.MoveTo(SetupAssistantStep.ProviderSettings);
    }

    private static void ProviderSettings(SetupAssistantStepContext step)
    {
        var token = step.Field("service_token");
        var confirmation = step.Field("service_token_confirm");

        if (!SetupAssistantInputs.IsSecret(token))
        {
            step.Session.Reject(SetupAssistantRejection.SecretTooShort);
            return;
        }

        if (!string.Equals(token, confirmation, StringComparison.Ordinal))
        {
            step.Session.Reject(SetupAssistantRejection.SecretMismatch);
            return;
        }

        step.Session.SetServiceToken(token);
        step.Session.MoveTo(
            step.Session.Mode == SetupMode.LocalMailpit
                ? SetupAssistantStep.ApplyConfirmation
                : SetupAssistantStep.AcsSettings);
    }

    private static void AcsSettings(SetupAssistantStepContext step)
    {
        var connectionString = step.Field("acs_connection_string");
        var confirmation = step.Field("acs_connection_string_confirm");
        var displayName = step.Field("platform_sender_display_name");

        if (!SetupAssistantInputs.IsSecret(connectionString))
        {
            step.Session.Reject(SetupAssistantRejection.SecretTooShort);
            return;
        }

        if (!string.Equals(connectionString, confirmation, StringComparison.Ordinal))
        {
            step.Session.Reject(SetupAssistantRejection.SecretMismatch);
            return;
        }

        if (!SetupAssistantInputs.IsDisplayText(displayName))
        {
            step.Session.Reject(SetupAssistantRejection.MissingRequiredField);
            return;
        }

        step.Session.SetAcsCredentials(connectionString, confirmation);
        step.Session.SetPlatformSenderDisplayName(displayName);
        step.Session.MoveTo(SetupAssistantStep.ApplyConfirmation);
    }

    private static async Task ConfirmAsync(SetupAssistantStepContext step)
    {
        var session = step.Session;
        if (session.Mode is not { } mode || session.ServiceToken is null)
        {
            session.Reject(SetupAssistantRejection.StepNotAvailable);
            return;
        }

        if (step.Action == "retry")
        {
            session.MoveTo(SetupAssistantStep.ApplyConfirmation);
            return;
        }

        var environmentConfirmation = step.Field("environment_confirmation");
        var intentConfirmation = step.Field("intent_confirmation");
        if (mode != SetupMode.LocalMailpit
            && (!string.Equals(
                    environmentConfirmation,
                    SetupAssistantInputs.EnvironmentConfirmationFor(mode),
                    StringComparison.Ordinal)
                || !string.Equals(
                    intentConfirmation,
                    AcsRegisterOperation.IntentPhrase,
                    StringComparison.Ordinal)))
        {
            session.Reject(SetupAssistantRejection.ConfirmationPhraseMismatch);
            return;
        }

        var outcome = await step.Operations.ApplyMainSetupAsync(
            BuildMainSetupInput(session, mode, environmentConfirmation, intentConfirmation),
            step.CancellationToken);
        session.SetMainSetup(outcome);
        session.MoveTo(SetupAssistantStep.ApplyOutcome);
    }

    private static async Task VerifyAsync(SetupAssistantStepContext step)
    {
        var session = step.Session;
        if (!session.MainSetupSucceeded || session.Mode is not { } mode)
        {
            session.Reject(SetupAssistantRejection.StepNotAvailable);
            return;
        }

        switch (step.Action)
        {
            case "continue":
                session.MoveTo(SetupAssistantStep.DeploymentVerification);
                return;

            case "finish":
                session.MoveTo(SetupAssistantStep.MainSetupComplete);
                return;

            case "staging" when mode == SetupMode.StagingVerification:
                await RunStagingAsync(step, session);
                return;

            case "production" when mode == SetupMode.ProductionAcs:
                await RunProductionAsync(step, session);
                return;

            default:
                session.Reject(SetupAssistantRejection.StepNotAvailable);
                return;
        }
    }

    private static async Task RunStagingAsync(
        SetupAssistantStepContext step,
        SetupAssistantSession session)
    {
        var recipient = step.Field("recipient_email");
        if (!SetupAssistantInputs.IsEmail(recipient))
        {
            session.Reject(SetupAssistantRejection.InvalidEmail);
            return;
        }

        if (session.MainSetup?.AppliedProof is not { } proof)
        {
            session.Reject(SetupAssistantRejection.StepNotAvailable);
            return;
        }

        session.SetStagingRecipient(recipient);
        var outcome = await step.Operations.VerifyStagingAsync(
            new SetupAssistantStagingInput
            {
                TenantId = session.TenantId,
                RecipientEmail = recipient,
                EnvironmentConfirmation = step.Field("environment_confirmation"),
                IntentConfirmation = step.Field("intent_confirmation"),
                AssistantSessionId = session.SessionId,
                AppliedProof = proof,
            },
            step.CancellationToken);
        session.SetStaging(outcome);
        session.MoveTo(SetupAssistantStep.DeploymentVerification);
    }

    private static async Task RunProductionAsync(
        SetupAssistantStepContext step,
        SetupAssistantSession session)
    {
        if (session.MainSetup?.AppliedProof is not { } proof)
        {
            session.Reject(SetupAssistantRejection.StepNotAvailable);
            return;
        }

        var environmentConfirmation = step.Field("environment_confirmation");
        var approval = step.Field("live_sending_approval");
        if (!string.Equals(environmentConfirmation, AcsEnvironmentConfirmation.Production, StringComparison.Ordinal)
            || !string.Equals(approval, AcsLiveSendingApproval.EnablePhrase, StringComparison.Ordinal))
        {
            session.Reject(SetupAssistantRejection.ConfirmationPhraseMismatch);
            return;
        }

        var outcome = await step.Operations.EnableLiveSendingAsync(
            new SetupAssistantProductionInput
            {
                EnvironmentConfirmation = environmentConfirmation,
                LiveSendingEnableApproval = approval,
                AppliedProof = proof,
            },
            step.CancellationToken);
        session.SetMainSetup(outcome);
        session.MoveTo(
            outcome.Kind == SetupAssistantOutcomeKind.Succeeded
                ? SetupAssistantStep.MainSetupComplete
                : SetupAssistantStep.ApplyOutcome);
    }

    private static void AdminChoice(SetupAssistantStepContext step)
    {
        if (!step.Session.MainSetupSucceeded)
        {
            step.Session.Reject(SetupAssistantRejection.AdminRequiresMainSetup);
            return;
        }

        step.Session.MoveTo(SetupAssistantStep.AdminChoice);
    }

    private static async Task AdminPreflightAsync(SetupAssistantStepContext step)
    {
        var session = step.Session;
        if (!session.MainSetupSucceeded)
        {
            session.Reject(SetupAssistantRejection.AdminRequiresMainSetup);
            return;
        }

        if (step.Action == "open")
        {
            session.MoveTo(SetupAssistantStep.AdminAccessPreflight);
            return;
        }

        var profile = step.Field("profile") == "production-https"
            ? SetupAssistantAdminProfile.ProductionHttps
            : SetupAssistantAdminProfile.LocalDevelopment;
        var origin = step.Field("origin");
        var environmentName = step.Field("environment_name");
        var allowedLocalAddress = step.Field("allowed_local_address");

        if (!SetupAssistantInputs.IsAbsoluteOrigin(origin))
        {
            session.Reject(SetupAssistantRejection.InvalidOrigin);
            return;
        }

        if (!SetupAssistantInputs.IsIdentifier(environmentName)
            || !SetupAssistantInputs.IsIpAddress(allowedLocalAddress))
        {
            session.Reject(SetupAssistantRejection.MissingRequiredField);
            return;
        }

        session.SetAdminAccessInput(
            profile,
            origin,
            environmentName,
            step.Checkbox("loopback_only_published"),
            step.Checkbox("approved_reverse_proxy"),
            step.Checkbox("server_local_address_confirmed"),
            allowedLocalAddress);

        session.SetAdminPreflight(await step.Operations.CheckAdminAccessProfileAsync(
            BuildAdminAccessInput(session),
            step.CancellationToken));
        session.MoveTo(SetupAssistantStep.AdminAccessPreflight);
    }

    private static async Task AdminBootstrapAsync(SetupAssistantStepContext step)
    {
        var session = step.Session;
        if (!session.MainSetupSucceeded || session.AdminPreflight is not { Satisfied: true })
        {
            session.Reject(SetupAssistantRejection.AdminRequiresMainSetup);
            return;
        }

        if (step.Action == "open")
        {
            session.MoveTo(SetupAssistantStep.AdminBootstrapOutcome);
            return;
        }

        var username = step.Field("admin_username");
        var password = step.Field("admin_password");
        var confirmation = step.Field("admin_password_confirm");

        if (!SetupAssistantInputs.IsIdentifier(username))
        {
            session.Reject(SetupAssistantRejection.InvalidIdentifier);
            return;
        }

        if (!SetupAssistantInputs.IsSecret(password))
        {
            session.Reject(SetupAssistantRejection.SecretTooShort);
            return;
        }

        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            session.Reject(SetupAssistantRejection.SecretMismatch);
            return;
        }

        session.SetAdminCredentials(username, password);
        try
        {
            session.SetAdminBootstrap(await step.Operations.BootstrapAdminAsync(
                new SetupAssistantAdminBootstrapInput
                {
                    Access = BuildAdminAccessInput(session),
                    Username = username,
                    Password = session.AdminPassword!,
                    TenantIds = [session.TenantId],
                },
                step.CancellationToken));
        }
        finally
        {
            session.DiscardAdminPassword();
        }

        session.MoveTo(SetupAssistantStep.AdminBootstrapOutcome);
    }

    private static void Finish(SetupAssistantStepContext step)
    {
        var session = step.Session;
        if (step.Action == "skip")
        {
            session.SkipAdmin();
        }

        if (step.Action == "stop")
        {
            session.MoveTo(SetupAssistantStep.Cancelled);
            step.Sessions.Stop(SetupAssistantShutdownReason.Completed);
            return;
        }

        session.MoveTo(SetupAssistantStep.FinalGuidance);
    }

    private static SetupAssistantMainSetupInput BuildMainSetupInput(
        SetupAssistantSession session,
        SetupMode mode,
        string environmentConfirmation,
        string intentConfirmation)
    {
        var tokenEnv = SetupAssistantInputs.TokenEnvFor(mode);
        var tenants = new MailerTenantsFile
        {
            Version = 1,
            Environment = SetupAssistantInputs.EnvironmentFor(mode),
            Tenants =
            [
                new MailerTenant
                {
                    TenantId = session.TenantId,
                    Name = session.TenantName,
                    SourceServices = [session.SourceService],
                    DefaultFrom = new MailerAddress
                    {
                        Email = session.SenderEmail,
                        DisplayName = session.SenderDisplayName,
                    },
                    TokenEnv = tokenEnv,
                    Provider = SetupAssistantInputs.ProviderFor(mode),
                    LiveSending = false,

                    // Retry policy is a fixed Easy Setup default; the UI does not expose an
                    // arbitrary environment or options dictionary (ADR 0021 D-06).
                    Retry = new MailerRetryOptions
                    {
                        MaxAttempts = 5,
                        InitialDelaySeconds = 5,
                        MaxDelaySeconds = 300,
                    },
                },
            ],
        };

        return new SetupAssistantMainSetupInput
        {
            Mode = mode,
            Tenants = tenants,
            TokenSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [tokenEnv] = session.ServiceToken!.Reveal(),
            },
            AcsConnectionString = session.AcsConnectionString?.Reveal(),
            AcsConnectionStringConfirmation = session.AcsConnectionStringConfirmation?.Reveal(),
            PlatformSender = mode == SetupMode.LocalMailpit
                ? null
                : new SetupPlatformSenderInput
                {
                    Environment = SetupAssistantInputs.EnvironmentFor(mode),
                    Email = session.SenderEmail,
                    DisplayName = session.SenderDisplayName,
                },
            EnvironmentConfirmation = environmentConfirmation,
            IntentConfirmation = intentConfirmation,
        };
    }

    private static SetupAssistantAdminAccessInput BuildAdminAccessInput(SetupAssistantSession session) =>
        new()
        {
            Profile = session.AdminProfile,
            OriginText = session.AdminOriginText,
            EnvironmentName = session.AdminEnvironmentName,
            AllowedLocalAddress = session.AdminAllowedLocalAddress,
            AllowHttp = session.AdminProfile == SetupAssistantAdminProfile.LocalDevelopment,
            LoopbackOnlyPublished = session.AdminLoopbackOnlyPublished,
            ApprovedReverseProxy = session.AdminApprovedReverseProxy,
            ServerLocalAddressConfirmed = session.AdminServerLocalAddressConfirmed,
        };

    private static string DescribeExchange(SetupAssistantTokenExchange exchange) => exchange switch
    {
        SetupAssistantTokenExchange.AlreadyRedeemed =>
            "このトークンは既に使用済みです。Assistant を起動し直してください。",
        SetupAssistantTokenExchange.TokenExpired =>
            "トークンの有効期限が切れました。Assistant を起動し直してください。",
        SetupAssistantTokenExchange.SessionAlreadyActive =>
            "既に別の session が進行中です。同時に複数の session は開始できません。",
        _ => "トークンが一致しません。",
    };

    private static async Task<IFormCollection?> ReadFormAsync(HttpContext context)
    {
        if (!context.Request.HasFormContentType)
        {
            return null;
        }

        try
        {
            return await context.Request.ReadFormAsync(context.RequestAborted);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static async Task<bool> ValidateCsrfAsync(HttpContext context, SetupAssistantSession session)
    {
        var form = await ReadFormAsync(context);
        if (form is not null && SetupAssistantSecurity.ValidateCsrf(session, form))
        {
            return true;
        }

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await WriteHtmlAsync(
            context,
            SetupAssistantPages.RenderTerminated("要求を検証できませんでした。画面を開き直してください。"));
        return false;
    }

    private static void Redirect(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status303SeeOther;
        context.Response.Headers.Location = "/";
    }

    private static Task WriteHtmlAsync(HttpContext context, string html)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.WriteAsync(html);
    }
}

internal sealed class SetupAssistantStepContext(
    SetupAssistantSession session,
    IFormCollection form,
    SetupAssistantSessionManager sessions,
    ISetupAssistantOperations operations,
    CancellationToken cancellationToken)
{
    internal SetupAssistantSession Session { get; } = session;

    internal SetupAssistantSessionManager Sessions { get; } = sessions;

    internal ISetupAssistantOperations Operations { get; } = operations;

    internal CancellationToken CancellationToken { get; } = cancellationToken;

    internal string Action => Field("action");

    internal string Field(string name) => form[name].ToString();

    internal bool Checkbox(string name) =>
        string.Equals(Field(name), "true", StringComparison.Ordinal);
}
