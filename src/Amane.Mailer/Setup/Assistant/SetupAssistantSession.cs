namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// All operator input and workflow state for a single assistant run. Secrets and PII live only
/// here, in process memory, for the lifetime of the session. Nothing in this type is serialized,
/// written to disk, echoed into a URL, or placed in a cookie.
/// </summary>
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

    internal SetupAssistantMainSetupOutcome? MainSetup { get; private set; }

    internal SetupAssistantStagingOutcome? Staging { get; private set; }

    internal SetupAssistantAdminPreflightOutcome? AdminPreflight { get; private set; }

    internal SetupAssistantAdminBootstrapOutcome? AdminBootstrap { get; private set; }

    /// <summary>Non-null when the last transition was rejected. Always a fixed catalog key.</summary>
    internal string? InputRejectionKey { get; private set; }

    /// <summary>True once the main setup transaction reached a canonical success state.</summary>
    internal bool MainSetupSucceeded =>
        MainSetup is { Kind: SetupAssistantOutcomeKind.Succeeded, ConfigurationApplied: true };

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

    internal void SetMainSetup(SetupAssistantMainSetupOutcome outcome) => MainSetup = outcome;

    internal void SetStaging(SetupAssistantStagingOutcome outcome) => Staging = outcome;

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

    internal void SkipAdmin() => AdminSkipped = true;

    /// <summary>
    /// Drops operator secrets as soon as the step that needed them has completed, so a long-lived
    /// session does not keep ACS or Admin material in memory after it is no longer required.
    /// </summary>
    internal void DiscardAdminPassword()
    {
        AdminPassword?.Dispose();
        AdminPassword = null;
    }

    private SetupAssistantSecret Replace(SetupAssistantSecret? existing, string value)
    {
        if (existing is not null)
        {
            existing.Dispose();
            _secrets.Remove(existing);
        }

        var captured = SetupAssistantSecret.Capture(value);
        _secrets.Add(captured);
        return captured;
    }

    public void Dispose()
    {
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
