namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// All operator input and workflow state for a single assistant run. Secrets and PII live only
/// here, in process memory, for the lifetime of the session. Nothing in this type is serialized,
/// written to disk, echoed into a URL, or placed in a cookie.
/// </summary>
/// <remarks>
/// Clearing is best-effort. Form values arrive as immutable strings and the typed operations take
/// strings, so copies owned by the runtime cannot be overwritten; only the buffers this session
/// owns are zeroed, and process exit stays the final memory boundary.
/// </remarks>
internal sealed class SetupAssistantSession : IDisposable
{
    private readonly List<IDisposable> _secrets = [];

    internal SetupAssistantSession(string sessionId, string csrfToken, DateTimeOffset createdAt)
    {
        SessionId = sessionId;
        CsrfToken = csrfToken;
        CreatedAt = createdAt;
        LastSeenAt = createdAt;
    }

    internal string SessionId { get; }

    internal string CsrfToken { get; }

    internal DateTimeOffset CreatedAt { get; }

    internal DateTimeOffset LastSeenAt { get; private set; }

    internal SetupAssistantStep Step { get; private set; } = SetupAssistantStep.Welcome;

    internal SetupMode? Mode { get; private set; }

    internal Guid TenantId { get; private set; } = Guid.CreateVersion7();

    internal string TenantName { get; private set; } = string.Empty;

    internal string SourceService { get; private set; } = string.Empty;

    internal string SenderEmail { get; private set; } = string.Empty;

    internal string SenderDisplayName { get; private set; } = string.Empty;

    internal string StagingRecipientEmail { get; private set; } = string.Empty;

    internal string AdminUsername { get; private set; } = string.Empty;

    internal SetupAssistantAdminProfile AdminProfile { get; private set; }

    internal string AdminOriginText { get; private set; } = string.Empty;

    internal string AdminEnvironmentName { get; private set; } = string.Empty;

    internal string AdminAllowedLocalAddress { get; private set; } = string.Empty;

    internal bool AdminLoopbackOnlyPublished { get; private set; }

    internal bool AdminApprovedReverseProxy { get; private set; }

    internal bool AdminServerLocalAddressConfirmed { get; private set; }

    internal SetupAssistantSecret? ServiceToken { get; private set; }

    internal SetupAssistantSecret? AcsConnectionString { get; private set; }

    internal SetupAssistantSecret? AcsConnectionStringConfirmation { get; private set; }

    internal SetupAssistantSecret? AdminPassword { get; private set; }

    internal SetupAssistantDockerPreflightOutcome? DockerPreflight { get; private set; }

    /// <summary>
    /// Service-issued Main workflow authority. Outcomes below are mirrored from this state for
    /// existing presenters and transition gates.
    /// </summary>
    internal ISetupAssistantMainWorkflowState? MainWorkflow { get; private set; }

    internal SetupAssistantMainSetupOutcome? MainSetup => MainWorkflow?.MainSetup;

    internal SetupAssistantStagingOutcome? Staging => MainWorkflow?.Staging;

    /// <summary>
    /// The #451 live-sending enablement result. It is kept apart from <see cref="MainSetup"/> so a
    /// failed enablement cannot erase the successful apply that came before it.
    /// </summary>
    internal SetupAssistantMainSetupOutcome? LiveSending => MainWorkflow?.LiveSending;

    internal SetupAssistantAdminPreflightOutcome? AdminPreflight { get; private set; }

    internal SetupAssistantAdminBootstrapOutcome? AdminBootstrap { get; private set; }

    internal bool ApplyStarted { get; private set; }

    internal bool StagingSendStarted { get; private set; }

    internal bool LiveSendingPromotionStarted { get; private set; }

    internal bool AdminBootstrapStarted { get; private set; }

    internal bool PotentialSideEffectsStarted =>
        ApplyStarted || StagingSendStarted || LiveSendingPromotionStarted || AdminBootstrapStarted;

    internal bool IsDisposed { get; private set; }

    /// <summary>Non-null when the last transition was rejected. Always a fixed catalog key.</summary>
    internal string? InputRejectionKey { get; private set; }

    internal bool ConfigurationStageSucceeded =>
        MainWorkflow?.ConfigurationStageSucceeded == true;

    internal bool MainSetupSucceeded => ConfigurationStageSucceeded;

    /// <summary>True only when #451 reported the deployment as send-ready. Never inferred.</summary>
    internal bool DeploymentSendReady => MainWorkflow?.DeploymentSendReady == true;

    internal bool AdminSkipped { get; private set; }

    internal void Touch(DateTimeOffset now) => LastSeenAt = now;

    internal void MoveTo(SetupAssistantStep step)
    {
        Step = step;
        InputRejectionKey = null;
    }

    internal void Reject(string rejectionKey) => InputRejectionKey = rejectionKey;

    internal void SetMode(SetupMode mode)
    {
        Mode = mode;
        TenantId = Guid.CreateVersion7();
    }

    internal void SetTenantBasics(
        string tenantName,
        string sourceService,
        string senderEmail,
        string senderDisplayName)
    {
        TenantName = tenantName;
        SourceService = sourceService;
        SenderEmail = senderEmail;
        SenderDisplayName = senderDisplayName;
    }

    internal void SetServiceToken(string token) => ServiceToken = Replace(ServiceToken, token);

    internal void SetAcsCredentials(string connectionString, string confirmation)
    {
        AcsConnectionString = Replace(AcsConnectionString, connectionString);
        AcsConnectionStringConfirmation = Replace(AcsConnectionStringConfirmation, confirmation);
    }

    internal void SetPlatformSenderDisplayName(string displayName) => SenderDisplayName = displayName;

    internal void SetStagingRecipient(string recipientEmail) => StagingRecipientEmail = recipientEmail;

    internal void SetDockerPreflight(SetupAssistantDockerPreflightOutcome outcome) =>
        DockerPreflight = outcome;

    internal void EnsureMainWorkflow(SetupMode mode)
    {
        if (MainWorkflow is null || MainWorkflow.Mode != mode)
        {
            MainWorkflow = SetupAssistantMainSetupOrchestrator.CreateInitial(mode);
        }

        if (DockerPreflight is { Passed: true } && !MainWorkflow.SkipDockerPreflight)
        {
            MainWorkflow = SetupAssistantMainSetupOrchestrator.AcknowledgeDockerPreflight(
                MainWorkflow,
                DockerPreflight);
        }
    }

    internal void ApplyMainWorkflow(ISetupAssistantMainWorkflowState state) => MainWorkflow = state;

    internal void MarkApplyStarted() => ApplyStarted = true;

    internal void MarkStagingSendStarted() => StagingSendStarted = true;

    internal void MarkLiveSendingPromotionStarted() => LiveSendingPromotionStarted = true;

    internal void MarkAdminBootstrapStarted() => AdminBootstrapStarted = true;

    internal void ClearStagingForRetry()
    {
        StagingRecipientEmail = string.Empty;
        if (MainWorkflow is null || MainWorkflow.Staging is null)
        {
            return;
        }

        // Service-owned transition: drop staging outcome, keep AppliedProof.
        MainWorkflow = SetupAssistantMainSetupOrchestrator.PrepareStagingRetry(MainWorkflow);
    }

    internal void SetAdminAccessInput(
        SetupAssistantAdminProfile profile,
        string originText,
        string environmentName,
        bool loopbackOnlyPublished,
        bool approvedReverseProxy,
        bool serverLocalAddressConfirmed,
        string allowedLocalAddress)
    {
        AdminProfile = profile;
        AdminOriginText = originText;
        AdminEnvironmentName = environmentName;
        AdminAllowedLocalAddress = allowedLocalAddress;
        AdminLoopbackOnlyPublished = loopbackOnlyPublished;
        AdminApprovedReverseProxy = approvedReverseProxy;
        AdminServerLocalAddressConfirmed = serverLocalAddressConfirmed;
    }

    internal void SetAdminCredentials(string username, string password)
    {
        AdminUsername = username;
        AdminPassword = Replace(AdminPassword, password);
    }

    internal void SetAdminPreflight(SetupAssistantAdminPreflightOutcome outcome) =>
        AdminPreflight = outcome;

    internal void SetAdminBootstrap(SetupAssistantAdminBootstrapOutcome outcome) =>
        AdminBootstrap = outcome;

    /// <summary>
    /// Clears a recorded Admin bootstrap outcome so managed same-user reapply can submit again.
    /// Does not reset <see cref="AdminBootstrapStarted"/> — side-effect honesty stays intact.
    /// </summary>
    internal void ClearAdminBootstrapForRetry() => AdminBootstrap = null;

    internal void SkipAdmin() => AdminSkipped = true;

    /// <summary>
    /// Drops operator secrets as soon as the step that needed them has completed, so a long-lived
    /// session does not keep ACS or Admin material in memory after it is no longer required.
    /// </summary>
    internal void DiscardAdminPassword()
    {
        Release(AdminPassword);
        AdminPassword = null;
    }

    /// <summary>
    /// Releases the provider and ACS material once the apply has succeeded. Every later stage works
    /// from the #451 applied proof, so the assistant has no reason to keep credentials reachable.
    /// </summary>
    internal void DiscardApplySecrets()
    {
        Release(ServiceToken);
        Release(AcsConnectionString);
        Release(AcsConnectionStringConfirmation);
        ServiceToken = null;
        AcsConnectionString = null;
        AcsConnectionStringConfirmation = null;
    }

    private void Release(SetupAssistantSecret? secret)
    {
        if (secret is null)
        {
            return;
        }

        secret.Dispose();
        _secrets.Remove(secret);
    }

    private SetupAssistantSecret Replace(SetupAssistantSecret? existing, string value)
    {
        Release(existing);

        var captured = SetupAssistantSecret.Capture(value);
        _secrets.Add(captured);
        return captured;
    }

    public void Dispose()
    {
        IsDisposed = true;
        foreach (var secret in _secrets)
        {
            secret.Dispose();
        }

        _secrets.Clear();
        ServiceToken = null;
        AcsConnectionString = null;
        AcsConnectionStringConfirmation = null;
        AdminPassword = null;
    }
}
