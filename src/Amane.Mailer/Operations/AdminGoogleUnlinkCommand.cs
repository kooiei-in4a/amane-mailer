using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Operations;

public sealed class AdminGoogleUnlinkCommand(
    SqliteConnectionFactory connections,
    TimeProvider timeProvider)
{
    public const int SuccessExitCode = 0;
    public const int UnavailableExitCode = 1;
    public const int UsageErrorExitCode = 2;
    public const int NotFoundExitCode = 3;

    public static bool IsAdminGoogleUnlinkCommand(IReadOnlyList<string> args) =>
        args.Count >= 3
        && string.Equals(args[0], "admin", StringComparison.Ordinal)
        && string.Equals(args[1], "google", StringComparison.Ordinal)
        && string.Equals(args[2], "unlink", StringComparison.Ordinal);

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        string? parseError = null;
        if (!IsAdminGoogleUnlinkCommand(args)
            || !TryParseOptions(args, out var username, out parseError))
        {
            if (!string.IsNullOrWhiteSpace(parseError))
                await error.WriteLineAsync(parseError);

            await error.WriteLineAsync(
                "Usage: dotnet Amane.Mailer.dll admin google unlink --username <name>");
            return UsageErrorExitCode;
        }

        if (!await CanUseGoogleIdentityTableAsync(cancellationToken))
        {
            await error.WriteLineAsync("Mailer database schema is not migrated for Admin Google identities.");
            return UnavailableExitCode;
        }

        var repository = new AdminGoogleIdentityRepository(connections);
        var result = await repository.TryUnlinkByUsernameAsync(
            username,
            timeProvider.GetUtcNow(),
            AdminSessionRevokeReasons.GoogleIdentityUnlinked,
            cancellationToken);
        switch (result)
        {
            case AdminGoogleUnlinkResult.Unlinked:
                await output.WriteLineAsync(
                    $"Unlinked Google identity for admin user '{username}' and revoked active admin sessions.");
                return SuccessExitCode;
            case AdminGoogleUnlinkResult.UserNotFound:
                await error.WriteLineAsync($"Admin user '{username}' was not found.");
                return NotFoundExitCode;
            case AdminGoogleUnlinkResult.MappingNotFound:
                await error.WriteLineAsync(
                    $"No Google identity mapping found for admin user '{username}'.");
                return NotFoundExitCode;
            default:
                await error.WriteLineAsync("Google identity unlink failed.");
                return UnavailableExitCode;
        }
    }

    private async Task<bool> CanUseGoogleIdentityTableAsync(CancellationToken cancellationToken)
    {
        if (!await connections.CanConnectToMigratedSchemaAsync(cancellationToken))
            return false;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = 'admin_google_identities'
            LIMIT 1;
            """;
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static bool TryParseOptions(
        IReadOnlyList<string> args,
        out string username,
        out string? error)
    {
        username = string.Empty;
        error = null;
        string? parsedUsername = null;

        for (var index = 3; index < args.Count; index++)
        {
            var option = args[index];
            if (index + 1 >= args.Count)
            {
                error = $"Missing value for {option}.";
                return false;
            }

            var value = args[++index];
            switch (option)
            {
                case "--username":
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        error = "--username must not be empty.";
                        return false;
                    }

                    parsedUsername = value.Trim();
                    break;

                default:
                    error = $"Unknown option: {option}.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(parsedUsername))
        {
            error = "--username is required.";
            return false;
        }

        username = parsedUsername;
        return true;
    }
}
