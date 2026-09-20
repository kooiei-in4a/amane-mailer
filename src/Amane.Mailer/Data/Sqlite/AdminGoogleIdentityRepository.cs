using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Data.Sqlite;

public enum AdminGoogleLinkResult
{
    Linked,
    IdentityAlreadyLinked,
    AdminAlreadyLinked,
}

/// <summary>
/// Persists the explicit Google issuer+subject mapping onto an existing admin user.
/// Overwrite and remapping are never automatic.
/// </summary>
public sealed class AdminGoogleIdentityRepository(SqliteConnectionFactory connections)
{
    public async Task<AdminGoogleIdentityRow?> FindByIssuerSubjectAsync(
        string issuer,
        string subject,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT admin_user_id, issuer, subject, created_at
            FROM admin_google_identities
            WHERE issuer = @Issuer AND subject = @Subject
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@Issuer", issuer);
        command.Parameters.AddWithValue("@Subject", subject);
        return await ReadSingleAsync(command, cancellationToken);
    }

    public async Task<AdminGoogleIdentityRow?> FindByAdminUserIdAsync(
        long adminUserId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT admin_user_id, issuer, subject, created_at
            FROM admin_google_identities
            WHERE admin_user_id = @AdminUserId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@AdminUserId", adminUserId);
        return await ReadSingleAsync(command, cancellationToken);
    }

    public async Task<AdminGoogleLinkResult> TryLinkAsync(
        long adminUserId,
        string issuer,
        string subject,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO admin_google_identities (
                        admin_user_id, issuer, subject, created_at)
                    VALUES (
                        @AdminUserId, @Issuer, @Subject, @CreatedAt);
                    """;
                insert.Parameters.AddWithValue("@AdminUserId", adminUserId);
                insert.Parameters.AddWithValue("@Issuer", issuer);
                insert.Parameters.AddWithValue("@Subject", subject);
                insert.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(createdAt));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return AdminGoogleLinkResult.Linked;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            await transaction.RollbackAsync(cancellationToken);
            if (await FindByIssuerSubjectAsync(issuer, subject, cancellationToken) is not null)
                return AdminGoogleLinkResult.IdentityAlreadyLinked;

            return AdminGoogleLinkResult.AdminAlreadyLinked;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<AdminGoogleIdentityRow?> ReadSingleAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new AdminGoogleIdentityRow(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            SqliteTime.FromStorage(reader.GetString(3)));
    }
}
