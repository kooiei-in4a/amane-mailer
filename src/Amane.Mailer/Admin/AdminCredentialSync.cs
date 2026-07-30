using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Configuration;

namespace Amane.Mailer.Admin;

/// <summary>
/// Synchronizes env password hash with <c>admin_config</c> and revokes stale sessions (ADR 0014 D-01).
/// </summary>
public sealed class AdminCredentialSync
{
    private readonly AdminSessionRepository _sessions;
    private readonly AdminUserRepository _users;
    private readonly MailerTenantRegistry _tenantRegistry;
    private readonly MailerAdminOptions _options;
    private readonly IConfiguration? _configuration;
    private readonly AdminBootstrapDatabase? _bootstrapDatabase;
    private int _credentialEpoch;

    public AdminCredentialSync(
        AdminSessionRepository sessions,
        AdminUserRepository users,
        MailerTenantRegistry tenantRegistry,
        MailerAdminOptions options)
        : this(sessions, users, tenantRegistry, options, null, null)
    {
    }

    internal AdminCredentialSync(
        AdminSessionRepository sessions,
        AdminUserRepository users,
        MailerTenantRegistry tenantRegistry,
        MailerAdminOptions options,
        IConfiguration? configuration,
        AdminBootstrapDatabase? bootstrapDatabase)
    {
        _sessions = sessions;
        _users = users;
        _tenantRegistry = tenantRegistry;
        _options = options;
        _configuration = configuration;
        _bootstrapDatabase = bootstrapDatabase;
    }

    public int CredentialEpoch => _credentialEpoch;

    public async Task EnsureSyncedAsync(CancellationToken cancellationToken = default)
    {
        var tenantIds = _tenantRegistry.ListTenants().Select(static tenant => tenant.TenantId).ToArray();
        if (_configuration is not null
            && _bootstrapDatabase is not null
            && AdminBootstrapDatabase.LoadCurrentExpectation(_configuration) is { } expectation)
        {
            _credentialEpoch = await _bootstrapDatabase.EnsureExpectedStateAsync(
                expectation,
                _options.Username,
                _options.PasswordHash,
                tenantIds,
                cancellationToken);
            return;
        }

        var config = await _sessions.GetOrInitializeConfigAsync(_options.PasswordHash, cancellationToken);
        if (!string.Equals(config.AppliedPasswordHash, _options.PasswordHash, StringComparison.Ordinal))
        {
            var rotated = await _sessions.RotateCredentialAsync(
                _options.PasswordHash,
                config.CredentialEpoch + 1,
                AdminSessionRevokeReasons.CredentialChanged,
                cancellationToken);
            _credentialEpoch = rotated.CredentialEpoch;
            await _users.EnsureSeedUserAsync(
                _options.Username,
                _options.PasswordHash,
                tenantIds,
                cancellationToken);
            await _users.EnsureTenantScopeReadyAsync(
                tenantIds,
                cancellationToken);
            return;
        }

        _credentialEpoch = config.CredentialEpoch;
        await _users.EnsureSeedUserAsync(
            _options.Username,
            _options.PasswordHash,
            tenantIds,
            cancellationToken);
        await _users.EnsureTenantScopeReadyAsync(
            tenantIds,
            cancellationToken);
    }
}
