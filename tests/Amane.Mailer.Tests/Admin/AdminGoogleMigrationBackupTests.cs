using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminGoogleMigrationBackupTests
{
    [Fact]
    public async Task Fresh_database_applies_migration_021()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-google-mig-fresh", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        try
        {
            var factory = CreateFactory(databasePath);
            var applied = await new SqlMigrationRunner(factory).ApplyPendingAsync(ct);
            Assert.Contains("021_admin_google_identities.sql", applied);
            Assert.True(await new SqlMigrationRunner(factory).IsCurrentSchemaReadyAsync(ct));
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);
            Assert.True(await TableExistsAsync(connection, "admin_google_identities", ct));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task V21_database_upgrades_by_applying_021()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-google-mig-upgrade", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var migrationDirectory = Path.Combine(root, "migrations");
        try
        {
            await ApplyMigrationsThroughAsync(databasePath, migrationDirectory, "020_instance_configuration.sql", ct);
            var factory = CreateFactory(databasePath);
            var applied = await new SqlMigrationRunner(factory).ApplyPendingAsync(ct);
            Assert.Equal(["021_admin_google_identities.sql"], applied);
            Assert.True(await new SqlMigrationRunner(factory).IsCurrentSchemaReadyAsync(ct));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Unique_constraints_reject_duplicate_issuer_subject_and_admin_user()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-google-unique", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        try
        {
            var factory = CreateFactory(databasePath);
            await new SqlMigrationRunner(factory).ApplyPendingAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var repository = new AdminGoogleIdentityRepository(factory);
            var first = await repository.TryLinkAsync(await InsertAdminAsync(databasePath, "google-one", ct), AdminGoogleAuthenticationConstants.Issuer, "subject-a", now, ct);
            var secondUser = await InsertAdminAsync(databasePath, "google-two", ct);
            var duplicateIdentity = await repository.TryLinkAsync(secondUser, AdminGoogleAuthenticationConstants.Issuer, "subject-a", now, ct);
            var duplicateUser = await repository.TryLinkAsync(await GetUserIdAsync(databasePath, "google-one", ct), AdminGoogleAuthenticationConstants.Issuer, "subject-b", now, ct);

            Assert.Equal(AdminGoogleLinkResult.Linked, first);
            Assert.Equal(AdminGoogleLinkResult.IdentityAlreadyLinked, duplicateIdentity);
            Assert.Equal(AdminGoogleLinkResult.AdminAlreadyLinked, duplicateUser);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Whole_db_backup_includes_google_mapping()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-google-backup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var backupPath = Path.Combine(root, "backups", "mailer.db");
        try
        {
            var factory = CreateFactory(databasePath);
            await new SqlMigrationRunner(factory).ApplyPendingAsync(ct);
            var userId = await InsertAdminAsync(databasePath, "backup-admin", ct);
            var linked = await new AdminGoogleIdentityRepository(factory).TryLinkAsync(
                userId,
                AdminGoogleAuthenticationConstants.Issuer,
                "backup-subject-not-real",
                DateTimeOffset.UtcNow,
                ct);
            Assert.Equal(AdminGoogleLinkResult.Linked, linked);

            await factory.BackupToAsync(backupPath, ct);

            await using var connection = new SqliteConnection($"Data Source={backupPath};Pooling=False");
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT admin_user_id, issuer, subject
                FROM admin_google_identities
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            Assert.True(await reader.ReadAsync(ct));
            Assert.Equal(userId, reader.GetInt64(0));
            Assert.Equal(AdminGoogleAuthenticationConstants.Issuer, reader.GetString(1));
            Assert.Equal("backup-subject-not-real", reader.GetString(2));
            Assert.False(await reader.ReadAsync(ct));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Google_options_tostring_does_not_include_client_secret()
    {
        var options = AdminGoogleOptions.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AMANE_ADMIN_GOOGLE_CLIENT_ID"] = "amane-mailer-test-google-client-id.apps.googleusercontent.com",
                ["AMANE_ADMIN_GOOGLE_CLIENT_SECRET"] = "amane-mailer-test-google-client-secret-not-real",
            })
            .Build());

        Assert.True(options.Enabled);
        Assert.DoesNotContain("amane-mailer-test-google-client-secret-not-real", options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ClientSecret", options.ToString(), StringComparison.Ordinal);
    }

    private static SqliteConnectionFactory CreateFactory(string databasePath) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = $"Data Source={databasePath};Pooling=False",
            })
            .Build());

    private static async Task ApplyMigrationsThroughAsync(
        string databasePath,
        string migrationDirectory,
        string throughMigrationFileName,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(migrationDirectory);
        var source = Path.Combine(AppContext.BaseDirectory, "Data", "Migrations");
        var keep = true;
        foreach (var file in Directory.GetFiles(source, "*.sql").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(file);
            if (!keep)
                continue;

            File.Copy(file, Path.Combine(migrationDirectory, fileName), overwrite: true);
            if (string.Equals(fileName, throughMigrationFileName, StringComparison.Ordinal))
                keep = false;
        }

        await new SqlMigrationRunner(CreateFactory(databasePath), migrationDirectory)
            .ApplyPendingAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @Name LIMIT 1;";
        command.Parameters.AddWithValue("@Name", tableName);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<long> InsertAdminAsync(
        string databasePath,
        string username,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO admin_users (
                username, password_hash, disabled, credential_epoch,
                is_break_glass, is_instance_owner, created_at, updated_at)
            VALUES (
                @Username, @PasswordHash, 0, 0, 0, 0, @Now, @Now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@Username", username);
        command.Parameters.AddWithValue("@PasswordHash", AdminPasswordHasher.Hash("migration-password-not-real"));
        command.Parameters.AddWithValue("@Now", "2026-09-20T00:00:00.0000000Z");
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> GetUserIdAsync(
        string databasePath,
        string username,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM admin_users WHERE username = @Username;";
        command.Parameters.AddWithValue("@Username", username);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }
}
