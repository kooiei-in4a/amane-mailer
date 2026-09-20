using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailerAdminGoogleUnlinkCliTests
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-000000000101");
    private const string SubjectA = "google-subject-cli-a";
    private const string SubjectB = "google-subject-cli-b";
    private const string EmailCanary = "google-cli-canary@example.invalid";

    [Fact]
    public async Task Admin_google_unlink_reports_usage_for_missing_username()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var configuration = BuildConfiguration(root);
        try
        {
            await MigrateAsync(configuration, ct);
            var error = new StringWriter();
            var exitCode = await MailerCliHost.RunAdminGoogleUnlinkAsync(
                configuration,
                ["admin", "google", "unlink"],
                new StringWriter(),
                error,
                ct);

            Assert.Equal(AdminGoogleUnlinkCommand.UsageErrorExitCode, exitCode);
            Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
            Assert.Contains("admin google unlink --username", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Admin_google_unlink_reports_not_found_for_unknown_username()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var configuration = BuildConfiguration(root);
        try
        {
            await MigrateAsync(configuration, ct);
            var error = new StringWriter();
            var exitCode = await MailerCliHost.RunAdminGoogleUnlinkAsync(
                configuration,
                ["admin", "google", "unlink", "--username", "missing-admin"],
                new StringWriter(),
                error,
                ct);

            Assert.Equal(AdminGoogleUnlinkCommand.NotFoundExitCode, exitCode);
            Assert.Contains("was not found", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Admin_google_unlink_is_deterministic_when_mapping_is_absent()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var configuration = BuildConfiguration(root);
        const string username = "unlinked-cli-admin";
        try
        {
            await MigrateAsync(configuration, ct);
            await CreateScopedAdminAsync(configuration, username, ct);
            var error = new StringWriter();
            var exitCode = await MailerCliHost.RunAdminGoogleUnlinkAsync(
                configuration,
                ["admin", "google", "unlink", "--username", username],
                new StringWriter(),
                error,
                ct);

            Assert.Equal(AdminGoogleUnlinkCommand.NotFoundExitCode, exitCode);
            Assert.Contains("No Google identity mapping found", error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(SubjectA, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(EmailCanary, error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Admin_google_unlink_deletes_mapping_and_revokes_sessions_without_leaking_identity()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var configuration = BuildConfiguration(root);
        const string username = "linked-cli-admin";
        var factory = new SqliteConnectionFactory(configuration);
        var now = new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);
        try
        {
            await MigrateAsync(configuration, ct);
            var userId = await CreateScopedAdminAsync(configuration, username, ct);
            var identities = new AdminGoogleIdentityRepository(factory);
            Assert.Equal(
                AdminGoogleLinkResult.Linked,
                await identities.TryLinkAsync(
                    userId,
                    AdminGoogleAuthenticationConstants.Issuer,
                    SubjectA,
                    now,
                    ct));
            var sessions = new AdminSessionRepository(factory);
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

            var output = new StringWriter();
            var error = new StringWriter();
            var exitCode = await MailerCliHost.RunAdminGoogleUnlinkAsync(
                configuration,
                ["admin", "google", "unlink", "--username", username],
                output,
                error,
                ct);

            Assert.Equal(AdminGoogleUnlinkCommand.SuccessExitCode, exitCode);
            Assert.Contains("Unlinked Google identity", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(SubjectA, output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(EmailCanary, output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(SubjectA, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(EmailCanary, error.ToString(), StringComparison.Ordinal);
            Assert.Null(await identities.FindByIssuerSubjectAsync(
                AdminGoogleAuthenticationConstants.Issuer,
                SubjectA,
                ct));
            var session = await sessions.GetSessionAsync(sessionId, ct);
            Assert.NotNull(session?.RevokedAt);
            Assert.Equal(AdminSessionRevokeReasons.GoogleIdentityUnlinked, session.RevokeReason);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Admin_google_unlink_does_not_change_another_admin_mapping_or_session()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var configuration = BuildConfiguration(root);
        const string usernameA = "google-cli-admin-a";
        const string usernameB = "google-cli-admin-b";
        var factory = new SqliteConnectionFactory(configuration);
        var now = new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);
        try
        {
            await MigrateAsync(configuration, ct);
            var userA = await CreateScopedAdminAsync(configuration, usernameA, ct);
            var userB = await CreateScopedAdminAsync(configuration, usernameB, ct);
            var identities = new AdminGoogleIdentityRepository(factory);
            Assert.Equal(
                AdminGoogleLinkResult.Linked,
                await identities.TryLinkAsync(
                    userA,
                    AdminGoogleAuthenticationConstants.Issuer,
                    SubjectA,
                    now,
                    ct));
            Assert.Equal(
                AdminGoogleLinkResult.Linked,
                await identities.TryLinkAsync(
                    userB,
                    AdminGoogleAuthenticationConstants.Issuer,
                    SubjectB,
                    now,
                    ct));
            var sessions = new AdminSessionRepository(factory);
            var sessionB = await sessions.CreateSessionAsync(
                new AdminSessionRow(
                    Guid.NewGuid().ToString("N"),
                    usernameB,
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
                AdminGoogleUnlinkCommand.SuccessExitCode,
                await MailerCliHost.RunAdminGoogleUnlinkAsync(
                    configuration,
                    ["admin", "google", "unlink", "--username", usernameA],
                    new StringWriter(),
                    new StringWriter(),
                    ct));

            Assert.Null(await identities.FindByIssuerSubjectAsync(
                AdminGoogleAuthenticationConstants.Issuer,
                SubjectA,
                ct));
            var remaining = await identities.FindByIssuerSubjectAsync(
                AdminGoogleAuthenticationConstants.Issuer,
                SubjectB,
                ct);
            Assert.NotNull(remaining);
            Assert.Equal(userB, remaining.AdminUserId);
            var otherSession = await sessions.GetSessionAsync(sessionB, ct);
            Assert.NotNull(otherSession);
            Assert.Null(otherSession.RevokedAt);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task<long> CreateScopedAdminAsync(
        IConfiguration configuration,
        string username,
        CancellationToken cancellationToken)
    {
        var exitCode = await MailerCliHost.RunAdminUserCreateAsync(
            configuration,
            [
                "admin", "user", "create",
                "--username", username,
                "--password-hash", AdminPasswordHasher.Hash("google-cli-password-not-real"),
                "--tenant-id", TenantA.ToString("D"),
            ],
            new StringWriter(),
            new StringWriter(),
            cancellationToken);
        Assert.Equal(AdminUserCreateCommand.SuccessExitCode, exitCode);
        var repository = new AdminUserRepository(
            new SqliteConnectionFactory(configuration),
            TimeProvider.System);
        var user = await repository.GetActiveUserByUsernameAsync(username, cancellationToken);
        Assert.NotNull(user);
        return user.Id;
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-admin-google-unlink-cli", Guid.NewGuid().ToString("N"));
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
