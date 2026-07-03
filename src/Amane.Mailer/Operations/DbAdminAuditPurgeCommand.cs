using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Operations;

public sealed class DbAdminAuditPurgeCommand(
    SqliteConnectionFactory connections,
    TimeProvider timeProvider)
{
    public const int SuccessExitCode = 0;
    public const int UnavailableExitCode = 1;
    public const int UsageErrorExitCode = 2;

    public static bool IsDbAdminAuditPurgeCommand(IReadOnlyList<string> args) =>
        args.Count >= 3
        && string.Equals(args[0], "db", StringComparison.Ordinal)
        && string.Equals(args[1], "admin-audit", StringComparison.Ordinal)
        && string.Equals(args[2], "purge", StringComparison.Ordinal);

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        IConfiguration configuration,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        string? parseError = null;
        if (!IsDbAdminAuditPurgeCommand(args)
            || !TryParseOptions(args, out var olderThanDays, out parseError))
        {
            if (!string.IsNullOrWhiteSpace(parseError))
            {
                await error.WriteLineAsync(parseError);
            }

            await error.WriteLineAsync(
                "Usage: dotnet Amane.Mailer.dll db admin-audit purge --older-than-days <days>");
            return UsageErrorExitCode;
        }

        var retentionOptions = MailerAdminAuditRetentionOptions.Load(configuration);
        try
        {
            retentionOptions.ValidatePurgeDays(olderThanDays);
        }
        catch (InvalidOperationException ex)
        {
            await error.WriteLineAsync(ex.Message);
            return UsageErrorExitCode;
        }

        if (!await CanUseAdminAuditTableAsync(cancellationToken))
        {
            await error.WriteLineAsync("Mailer database schema is not migrated for admin audit events.");
            return UnavailableExitCode;
        }

        var repository = new AdminAuditRepository(connections);
        var cutoff = timeProvider.GetUtcNow().AddDays(-olderThanDays);
        var totalDeleted = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var deleted = await repository.DeleteOlderThanAsync(
                cutoff,
                retentionOptions.BatchSize,
                cancellationToken);

            if (deleted == 0)
            {
                break;
            }

            totalDeleted += deleted;
        }

        await output.WriteLineAsync(
            $"Purge removed {totalDeleted} admin audit events older than {olderThanDays} days.");
        return SuccessExitCode;
    }

    private async Task<bool> CanUseAdminAuditTableAsync(CancellationToken cancellationToken)
    {
        if (!await connections.CanConnectToMigratedSchemaAsync(cancellationToken))
            return false;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = 'admin_audit_events'
            LIMIT 1;
            """;
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static bool TryParseOptions(
        IReadOnlyList<string> args,
        out int olderThanDays,
        out string? error)
    {
        olderThanDays = 0;
        var found = false;

        for (var index = 3; index < args.Count; index++)
        {
            var option = args[index];
            if (!string.Equals(option, "--older-than-days", StringComparison.Ordinal))
            {
                error = $"Unknown option: {option}.";
                return false;
            }

            if (index + 1 >= args.Count)
            {
                error = "Missing value for --older-than-days.";
                return false;
            }

            var value = args[++index];
            if (!int.TryParse(value, out olderThanDays) || olderThanDays < 1)
            {
                error = "--older-than-days must be a positive integer.";
                return false;
            }

            found = true;
        }

        if (!found)
        {
            error = "--older-than-days is required.";
            return false;
        }

        error = null;
        return true;
    }
}
