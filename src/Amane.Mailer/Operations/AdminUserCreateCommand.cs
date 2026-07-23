using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Operations;

public sealed class AdminUserCreateCommand(
    SqliteConnectionFactory connections,
    TimeProvider timeProvider)
{
    public const int SuccessExitCode = 0;
    public const int UnavailableExitCode = 1;
    public const int UsageErrorExitCode = 2;

    public static bool IsAdminUserCreateCommand(IReadOnlyList<string> args) =>
        args.Count >= 3
        && string.Equals(args[0], "admin", StringComparison.Ordinal)
        && string.Equals(args[1], "user", StringComparison.Ordinal)
        && string.Equals(args[2], "create", StringComparison.Ordinal);

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        string? parseError = null;
        if (!IsAdminUserCreateCommand(args)
            || !TryParseOptions(args, out var options, out parseError))
        {
            if (!string.IsNullOrWhiteSpace(parseError))
            {
                await error.WriteLineAsync(parseError);
            }

            await error.WriteLineAsync(
                "Usage: dotnet Amane.Mailer.dll admin user create --username <name> --password-hash <pbkdf2> "
                + "[--tenant-id <uuid> ...] [--break-glass]");
            return UsageErrorExitCode;
        }

        if (!await CanUseAdminUserTablesAsync(cancellationToken))
        {
            await error.WriteLineAsync("Mailer database schema is not migrated for admin users.");
            return UnavailableExitCode;
        }

        var repository = new AdminUserRepository(connections, timeProvider);
        if (options.IsBreakGlass)
        {
            await repository.CreateBreakGlassUserAsync(
                options.Username,
                options.PasswordHash,
                cancellationToken);
            await output.WriteLineAsync($"Created break-glass admin user '{options.Username}'.");
            return SuccessExitCode;
        }

        await repository.CreateOrUpdateScopedUserAsync(
            options.Username,
            options.PasswordHash,
            options.TenantIds,
            cancellationToken);
        await output.WriteLineAsync(
            $"Created scoped admin user '{options.Username}' with {options.TenantIds.Count} tenant scope(s).");
        return SuccessExitCode;
    }

    private async Task<bool> CanUseAdminUserTablesAsync(CancellationToken cancellationToken)
    {
        if (!await connections.CanConnectToMigratedSchemaAsync(cancellationToken))
            return false;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = 'admin_users'
            LIMIT 1;
            """;
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static bool TryParseOptions(
        IReadOnlyList<string> args,
        out AdminUserCreateOptions options,
        out string? error)
    {
        string? username = null;
        string? passwordHash = null;
        var tenantIds = new List<Guid>();
        var isBreakGlass = false;

        for (var index = 3; index < args.Count; index++)
        {
            var option = args[index];
            if (string.Equals(option, "--break-glass", StringComparison.Ordinal))
            {
                isBreakGlass = true;
                continue;
            }

            if (index + 1 >= args.Count)
            {
                options = default;
                error = $"Missing value for {option}.";
                return false;
            }

            var value = args[++index];
            switch (option)
            {
                case "--username":
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        options = default;
                        error = "--username must not be empty.";
                        return false;
                    }

                    username = value.Trim();
                    break;

                case "--password-hash":
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        options = default;
                        error = "--password-hash must not be empty.";
                        return false;
                    }

                    if (!AdminPasswordHasher.IsSupportedHash(value))
                    {
                        options = default;
                        error =
                            "--password-hash must be pbkdf2:sha256 with iterations "
                            + $"{AdminPasswordHasher.MinIterations}-{AdminPasswordHasher.MaxIterations}, "
                            + $"salt {AdminPasswordHasher.MinSaltSize}-{AdminPasswordHasher.MaxSaltSize} bytes, "
                            + $"and hash {AdminPasswordHasher.MinHashSize}-{AdminPasswordHasher.MaxHashSize} bytes "
                            + "(use 'admin hash-password'; legacy weaker hashes are rejected).";
                        return false;
                    }

                    passwordHash = value;
                    break;

                case "--tenant-id":
                    if (!Guid.TryParse(value, out var tenantId))
                    {
                        options = default;
                        error = "--tenant-id must be a UUID.";
                        return false;
                    }

                    tenantIds.Add(tenantId);
                    break;

                default:
                    options = default;
                    error = $"Unknown option: {option}.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(passwordHash))
        {
            options = default;
            error = "--username and --password-hash are required.";
            return false;
        }

        if (isBreakGlass && tenantIds.Count > 0)
        {
            options = default;
            error = "--break-glass cannot be combined with --tenant-id.";
            return false;
        }

        if (!isBreakGlass && tenantIds.Count == 0)
        {
            options = default;
            error = "At least one --tenant-id is required unless --break-glass is set.";
            return false;
        }

        options = new AdminUserCreateOptions(
            username,
            passwordHash,
            tenantIds.Distinct().ToArray(),
            isBreakGlass);
        error = null;
        return true;
    }

    private readonly record struct AdminUserCreateOptions(
        string Username,
        string PasswordHash,
        IReadOnlyList<Guid> TenantIds,
        bool IsBreakGlass);
}
