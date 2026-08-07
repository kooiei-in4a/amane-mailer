using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailerAdminUserCliTests
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-000000000101");
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000202");

    [Fact]
    public async Task Admin_user_create_command_reports_usage_for_missing_options()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var configuration = BuildConfiguration(root);

        try
        {
            await MigrateAsync(configuration, ct);
            var error = new StringWriter();

            var exitCode = await MailerCliHost.RunAdminUserCreateAsync(
                configuration,
                ["admin", "user", "create"],
                new StringWriter(),
                error,
                ct);

            Assert.Equal(AdminUserCreateCommand.UsageErrorExitCode, exitCode);
            Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Admin_user_create_command_rejects_legacy_weak_password_hash()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var configuration = BuildConfiguration(root);
        var weakHash = string.Join(
            ':',
            "pbkdf2",
            "sha256",
            "100000",
            Convert.ToBase64String(new byte[16]),
            Convert.ToBase64String(new byte[32]));
        var error = new StringWriter();

        try
        {
            await MigrateAsync(configuration, ct);

            var exitCode = await MailerCliHost.RunAdminUserCreateAsync(
                configuration,
                [
                    "admin", "user", "create",
                    "--username", "weak-hash-admin",
                    "--password-hash", weakHash,
                    "--tenant-id", TenantA.ToString("D"),
                ],
                new StringWriter(),
                error,
                ct);

            Assert.Equal(AdminUserCreateCommand.UsageErrorExitCode, exitCode);
            Assert.Contains("--password-hash", error.ToString(), StringComparison.Ordinal);
            Assert.Contains("legacy weaker hashes are rejected", error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Admin_user_create_command_creates_scoped_user()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var configuration = BuildConfiguration(root);
        const string username = "scoped-cli-admin";
        var passwordHash = AdminPasswordHasher.Hash("password-for-scoped-cli");

        try
        {
            await MigrateAsync(configuration, ct);
            var exitCode = await RunCreateAsync(
                configuration,
                [
                    "admin", "user", "create",
                    "--username", username,
                    "--password-hash", passwordHash,
                    "--tenant-id", TenantA.ToString("D"),
                    "--tenant-id", TenantB.ToString("D"),
                ],
                ct);

            Assert.Equal(AdminUserCreateCommand.SuccessExitCode, exitCode);

            var repository = new AdminUserRepository(
                new SqliteConnectionFactory(configuration),
                TimeProvider.System);
            var access = await repository.GetTenantAccessAsync(username, ct);

            Assert.NotNull(access);
            Assert.False(access.IsBreakGlass);
            Assert.Contains(TenantA, access.TenantIds);
            Assert.Contains(TenantB, access.TenantIds);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Admin_user_create_command_creates_break_glass_user()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var configuration = BuildConfiguration(root);
        const string username = "break-glass-cli-admin";
        var passwordHash = AdminPasswordHasher.Hash("password-for-break-glass-cli");

        try
        {
            await MigrateAsync(configuration, ct);
            var exitCode = await RunCreateAsync(
                configuration,
                [
                    "admin", "user", "create",
                    "--username", username,
                    "--password-hash", passwordHash,
                    "--break-glass",
                ],
                ct);

            Assert.Equal(AdminUserCreateCommand.SuccessExitCode, exitCode);

            var repository = new AdminUserRepository(
                new SqliteConnectionFactory(configuration),
                TimeProvider.System);
            var access = await repository.GetTenantAccessAsync(username, ct);

            Assert.NotNull(access);
            Assert.True(access.IsBreakGlass);
            Assert.Empty(access.TenantIds);

            Assert.False(await repository.HasCapabilityAsync(
                username,
                AdminCapabilities.BccRecipientReveal,
                ct));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Admin_user_create_command_accepts_inline_configuration_args()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var databasePath = Path.Combine(root, "mailer.db");
        var configuration = BuildConfiguration(root);
        const string username = "inline-config-admin";
        var passwordHash = AdminPasswordHasher.Hash("password-for-inline-config");

        try
        {
            await MigrateAsync(configuration, ct);
            var exitCode = await MailerCliHost.RunAdminUserCreateAsync(
                configuration,
                [
                    "admin", "user", "create",
                    "--username", username,
                    "--password-hash", passwordHash,
                    "--tenant-id", TenantA.ToString("D"),
                    $"ConnectionStrings:Mailer=Data Source={databasePath}",
                ],
                new StringWriter(),
                new StringWriter(),
                ct);

            Assert.Equal(AdminUserCreateCommand.SuccessExitCode, exitCode);

            var access = await new AdminUserRepository(
                    new SqliteConnectionFactory(configuration),
                    TimeProvider.System)
                .GetTenantAccessAsync(username, ct);

            Assert.NotNull(access);
            Assert.Contains(TenantA, access.TenantIds);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Admin_user_create_command_revokes_sessions_when_scoped_user_is_updated()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var configuration = BuildConfiguration(root);
        const string username = "revoked-cli-admin";
        var passwordHash = AdminPasswordHasher.Hash("password-for-revoked-cli");
        var factory = new SqliteConnectionFactory(configuration);
        var repository = new AdminUserRepository(factory, TimeProvider.System);
        var sessions = new AdminSessionRepository(factory);
        var now = new DateTimeOffset(2026, 7, 3, 6, 0, 0, TimeSpan.Zero);

        try
        {
            await MigrateAsync(configuration, ct);
            Assert.Equal(
                AdminUserCreateCommand.SuccessExitCode,
                await RunCreateAsync(
                    configuration,
                    [
                        "admin", "user", "create",
                        "--username", username,
                        "--password-hash", passwordHash,
                        "--tenant-id", TenantA.ToString("D"),
                    ],
                    ct));

            var sessionId = await sessions.CreateSessionAsync(
                new AdminSessionRow(
                    Guid.NewGuid().ToString("N"),
                    username,
                    now,
                    now,
                    now.AddHours(8),
                    now.AddMinutes(30),
                    null,
                    null,
                    0),
                maxConcurrentSessions: 3,
                ct);

            Assert.Equal(
                AdminUserCreateCommand.SuccessExitCode,
                await RunCreateAsync(
                    configuration,
                    [
                        "admin", "user", "create",
                        "--username", username,
                        "--password-hash", passwordHash,
                        "--tenant-id", TenantB.ToString("D"),
                    ],
                    ct));

            var session = await sessions.GetSessionAsync(sessionId, ct);
            Assert.NotNull(session?.RevokedAt);
            Assert.Equal(AdminSessionRevokeReasons.TenantScopeChanged, session.RevokeReason);

            var access = await repository.GetTenantAccessAsync(username, ct);
            Assert.NotNull(access);
            Assert.Single(access.TenantIds);
            Assert.Equal(TenantB, access.TenantIds.Single());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Admin_user_capability_command_grants_and_revokes_bcc_reveal()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var configuration = BuildConfiguration(root);
        const string username = "capability-cli-admin";

        try
        {
            await MigrateAsync(configuration, ct);
            Assert.Equal(
                AdminUserCreateCommand.SuccessExitCode,
                await RunCreateAsync(
                    configuration,
                    [
                        "admin", "user", "create",
                        "--username", username,
                        "--password-hash", AdminPasswordHasher.Hash("capability-cli-password"),
                        "--tenant-id", TenantA.ToString("D"),
                    ],
                    ct));

            var grantOutput = new StringWriter();
            Assert.Equal(
                AdminUserCapabilityCommand.SuccessExitCode,
                await MailerCliHost.RunAdminUserCapabilityAsync(
                    configuration,
                    [
                        "admin", "user", "capability", "grant",
                        "--username", username,
                        "--capability", AdminCapabilities.BccRecipientReveal,
                    ],
                    grantOutput,
                    new StringWriter(),
                    ct));
            Assert.Contains("granted", grantOutput.ToString(), StringComparison.OrdinalIgnoreCase);

            var repository = new AdminUserRepository(
                new SqliteConnectionFactory(configuration),
                TimeProvider.System);
            Assert.True(await repository.HasCapabilityAsync(username, AdminCapabilities.BccRecipientReveal, ct));

            var revokeOutput = new StringWriter();
            Assert.Equal(
                AdminUserCapabilityCommand.SuccessExitCode,
                await MailerCliHost.RunAdminUserCapabilityAsync(
                    configuration,
                    [
                        "admin", "user", "capability", "revoke",
                        "--username", username,
                        "--capability", AdminCapabilities.BccRecipientReveal,
                    ],
                    revokeOutput,
                    new StringWriter(),
                    ct));
            Assert.Contains("revoked", revokeOutput.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.False(await repository.HasCapabilityAsync(username, AdminCapabilities.BccRecipientReveal, ct));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Admin_user_capability_command_rejects_unknown_capability()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var configuration = BuildConfiguration(root);
        try
        {
            await MigrateAsync(configuration, ct);
            var error = new StringWriter();
            var exitCode = await MailerCliHost.RunAdminUserCapabilityAsync(
                configuration,
                [
                    "admin", "user", "capability", "grant",
                    "--username", "unknown-capability-admin",
                    "--capability", "view_unmasked_list_pii",
                ],
                new StringWriter(),
                error,
                ct);

            Assert.Equal(AdminUserCapabilityCommand.UsageErrorExitCode, exitCode);
            Assert.Contains("Unknown Admin capability is denied", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task<int> RunCreateAsync(
        IConfiguration configuration,
        IReadOnlyList<string> commandArgs,
        CancellationToken cancellationToken)
    {
        return await MailerCliHost.RunAdminUserCreateAsync(
            configuration,
            commandArgs,
            new StringWriter(),
            new StringWriter(),
            cancellationToken);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-admin-user-cli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static IConfiguration BuildConfiguration(string root) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = $"Data Source={Path.Combine(root, "mailer.db")}",
            })
            .Build();

    private static async Task MigrateAsync(IConfiguration configuration, CancellationToken cancellationToken)
    {
        var factory = new SqliteConnectionFactory(configuration);
        await new SqlMigrationRunner(factory).ApplyPendingAsync(cancellationToken);
    }

    private static void Cleanup(string root)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }
}
