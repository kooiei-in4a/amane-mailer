using Amane.Mailer.Configuration;

namespace Amane.Mailer.Setup;

/// <summary>
/// UI-independent Setup Core request. Adapters supply an already-resolved managed root;
/// Core rejects unsafe paths and never treats metadata as a runtime send authority.
/// </summary>
public sealed class SetupRequest
{
    public required SetupMode Mode { get; init; }

    /// <summary>
    /// Absolute managed root path resolved by the host adapter (not an arbitrary operator-typed path).
    /// </summary>
    public required string ManagedRootPath { get; init; }

    public bool DryRun { get; init; }

    public required MailerTenantsFile Tenants { get; init; }

    /// <summary>token_env name to secret token value. Values never enter fingerprint or public results.</summary>
    public IReadOnlyDictionary<string, string> TokenSecrets { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Optional metrics bearer. Secret-valued; never fingerprinted.</summary>
    public string? MetricsBearerToken { get; init; }

    /// <summary>ACS connection string for modes 2-4. File secret; never fingerprinted.</summary>
    public string? AcsConnectionString { get; init; }

    /// <summary>Platform sender representation for ACS modes. Not tenant send-path authority.</summary>
    public SetupPlatformSenderInput? PlatformSender { get; init; }

    /// <summary>
    /// Non-secret public env overrides limited to <see cref="ManagedEnvKeyCatalog.PublicEnvOverrideAllowlist"/>.
    /// Workflow-owned Admin/bounce/provider/path keys are rejected.
    /// </summary>
    public IReadOnlyDictionary<string, string> PublicEnvOverrides { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Admin non-secret representation contract for #459. Enabling Admin in Core is rejected;
    /// password handling remains out of scope.
    /// </summary>
    public SetupAdminBootstrapRepresentation? Admin { get; init; }

    /// <summary>
    /// Linux runtime ownership for generated bundle files (Mailer container UID/GID).
    /// Required for non-dry-run writes on Linux so the container can read finalized files.
    /// When the Setup process EUID differs from this UID and is not root, ownership assignment
    /// fails closed; host adapter / packaging (#449, #455) must document the supported operator model.
    /// </summary>
    public SetupRuntimeFileOwnership? RuntimeFileOwnership { get; init; }

    /// <summary>Optional image repository/tag recorded in compose.env and fingerprint.</summary>
    public string? ImageRepository { get; init; }

    public string? ImageTag { get; init; }
}

public sealed class SetupPlatformSenderInput
{
    public required string Environment { get; init; }
    public required string Email { get; init; }
    public required string DisplayName { get; init; }
}

/// <summary>
/// Non-secret Admin bootstrap representation owned as a typed contract for #459.
/// Does not accept password or password hash. Enabled=true is rejected by Setup Core.
/// </summary>
public sealed class SetupAdminBootstrapRepresentation
{
    public bool Enabled { get; init; }
    public string Username { get; init; } = "admin";
    public string AllowedLocalAddress { get; init; } = "127.0.0.1";
    public bool AllowHttp { get; init; }
    public string PiiListMode { get; init; } = "masked";
}

/// <summary>
/// Connects host-generated files to the Mailer container runtime identity on Linux.
/// Typical deploy image uses a non-root APP_UID (documented as 1654 for current tags).
/// </summary>
public sealed class SetupRuntimeFileOwnership
{
    public required uint UnixUserId { get; init; }
    public required uint UnixGroupId { get; init; }
}

/// <summary>ACS workflow typed input surface for #451 (no network / exact confirmation here).</summary>
public sealed class SetupAcsWorkflowInput
{
    public string? ConnectionString { get; init; }
    public SetupPlatformSenderInput? PlatformSender { get; init; }
}
