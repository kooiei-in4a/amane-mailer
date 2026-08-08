using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Operations;

public sealed class AdminUserCapabilityCommand(
    SqliteConnectionFactory connections,
    TimeProvider timeProvider)
{
    public const int SuccessExitCode = 0;
    public const int UnavailableExitCode = 1;
    public const int UsageErrorExitCode = 2;

    public static bool IsAdminUserCapabilityCommand(IReadOnlyList<string> args) =>
        args.Count >= 5
        && string.Equals(args[0], "admin", StringComparison.Ordinal)
        && string.Equals(args[1], "user", StringComparison.Ordinal)
        && string.Equals(args[2], "capability", StringComparison.Ordinal)
        && (string.Equals(args[3], "grant", StringComparison.Ordinal)
            || string.Equals(args[3], "revoke", StringComparison.Ordinal));

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptions(args, out var options, out var parseError))
        {
            if (!string.IsNullOrWhiteSpace(parseError))
                await error.WriteLineAsync(parseError);

            await error.WriteLineAsync(
                "Usage: dotnet Amane.Mailer.dll admin user capability "
                + "<grant|revoke> --username <name> --capability bcc_recipient_reveal");
            return UsageErrorExitCode;
        }

        if (!await CanUseCapabilityTableAsync(cancellationToken))
        {
            await error.WriteLineAsync("Mailer database schema is not migrated for Admin capabilities.");
            return UnavailableExitCode;
        }

        var repository = new AdminUserRepository(connections, timeProvider);
        try
        {
            var result = await repository.SetCapabilityAsync(
                options.Username,
                options.Capability,
                options.Grant,
                cancellationToken);

            var action = options.Grant ? "Granted" : "Revoked";
            var state = result == AdminCapabilityMutationResult.Unchanged
                ? (options.Grant ? "already granted" : "already absent")
                : action.ToLowerInvariant();
            await output.WriteLineAsync(
                $"Capability {options.Capability} for admin user '{options.Username}': {state}.");
            return SuccessExitCode;
        }
        catch (ArgumentException ex)
        {
            await error.WriteLineAsync(ex.Message);
            return UsageErrorExitCode;
        }
        catch (InvalidOperationException ex)
        {
            await error.WriteLineAsync(ex.Message);
            return UnavailableExitCode;
        }
    }

    private async Task<bool> CanUseCapabilityTableAsync(CancellationToken cancellationToken)
    {
        if (!await connections.CanConnectToMigratedSchemaAsync(cancellationToken))
            return false;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = 'admin_user_capabilities'
            LIMIT 1;
            """;
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static bool TryParseOptions(
        IReadOnlyList<string> args,
        out AdminUserCapabilityOptions options,
        out string? error)
    {
        options = default;
        error = null;
        if (!IsAdminUserCapabilityCommand(args))
        {
            error = "Invalid admin capability command.";
            return false;
        }

        string? username = null;
        string? capability = null;
        for (var index = 4; index < args.Count; index++)
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

                    username = value.Trim();
                    break;

                case "--capability":
                    if (!AdminCapabilities.IsKnownPersistent(value))
                    {
                        error = "Unknown Admin capability is denied.";
                        return false;
                    }

                    capability = value;
                    break;

                default:
                    error = $"Unknown option: {option}.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(capability))
        {
            error = "--username and --capability are required.";
            return false;
        }

        options = new AdminUserCapabilityOptions(
            username,
            capability,
            string.Equals(args[3], "grant", StringComparison.Ordinal));
        return true;
    }

    private readonly record struct AdminUserCapabilityOptions(
        string Username,
        string Capability,
        bool Grant);
}
