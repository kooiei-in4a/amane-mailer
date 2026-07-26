using Amane.Mailer.Admin;
using Amane.Mailer.Data;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Operations;

/// <summary>
/// Removes a tenant-scoped <c>mail_suppressions</c> row (issue #400 / ADR 0020 D-07).
/// Recipient normalization matches store (#301) and lookup (#303) via
/// <see cref="RecipientEmailNormalizer"/>. Stdout never echoes the recipient (ADR 0013).
/// </summary>
public sealed class DbSuppressionsRemoveCommand(
    SqliteConnectionFactory connections,
    TimeProvider timeProvider)
{
    public const int SuccessExitCode = 0;
    public const int UnavailableExitCode = 1;
    public const int UsageErrorExitCode = 2;
    public const int NotFoundExitCode = 3;

    public const string CliActor = "cli";

    public static bool IsDbSuppressionsRemoveCommand(IReadOnlyList<string> args) =>
        args.Count >= 3
        && string.Equals(args[0], "db", StringComparison.Ordinal)
        && string.Equals(args[1], "suppressions", StringComparison.Ordinal)
        && string.Equals(args[2], "remove", StringComparison.Ordinal);

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        string? parseError = null;
        if (!IsDbSuppressionsRemoveCommand(args)
            || !TryParseOptions(args, out var tenantId, out var recipient, out parseError))
        {
            if (!string.IsNullOrWhiteSpace(parseError))
            {
                await error.WriteLineAsync(parseError);
            }

            await error.WriteLineAsync(
                "Usage: dotnet Amane.Mailer.dll db suppressions remove --tenant-id <uuid> --recipient <email>");
            return UsageErrorExitCode;
        }

        if (!await CanUseSuppressionsTableAsync(cancellationToken))
        {
            await error.WriteLineAsync("Mailer database schema is not migrated for mail suppressions.");
            return UnavailableExitCode;
        }

        string normalized;
        try
        {
            normalized = RecipientEmailNormalizer.Normalize(recipient);
        }
        catch (ArgumentException)
        {
            await error.WriteLineAsync("--recipient must be a non-empty email address.");
            await error.WriteLineAsync(
                "Usage: dotnet Amane.Mailer.dll db suppressions remove --tenant-id <uuid> --recipient <email>");
            return UsageErrorExitCode;
        }

        var suppressions = new MailSuppressionRepository(connections);
        var removed = await suppressions.TryDeleteAsync(tenantId, normalized, cancellationToken);
        var occurredAt = timeProvider.GetUtcNow();

        if (removed)
        {
            await WriteAuditBestEffortAsync(
                tenantId,
                AdminAuditLog.Results.Success,
                errorCode: null,
                occurredAt,
                error,
                cancellationToken);

            await output.WriteLineAsync(
                $"Removed 1 mail suppression for tenant {tenantId:D}.");
            return SuccessExitCode;
        }

        await WriteAuditBestEffortAsync(
            tenantId,
            AdminAuditLog.Results.Failure,
            AdminAuditLog.ErrorCodes.NotFound,
            occurredAt,
            error,
            cancellationToken);

        await error.WriteLineAsync(
            $"No mail suppression found for tenant {tenantId:D}.");
        return NotFoundExitCode;
    }

    private async Task WriteAuditBestEffortAsync(
        Guid tenantId,
        string result,
        string? errorCode,
        DateTimeOffset occurredAt,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var auditEvent = new AdminAuditEvent
        {
            EventType = AdminAuditLog.EventTypes.MailSuppressionsRemoved,
            Actor = CliActor,
            OccurredAt = occurredAt,
            TargetType = AdminAuditLog.TargetTypes.MailSuppressions,
            TargetId = tenantId.ToString("D"),
            TenantId = tenantId,
            Result = result,
            ErrorCode = errorCode,
        };

        try
        {
            var repository = new AdminAuditRepository(connections);
            await repository.WriteAsync(
                AdminAuditLog.SanitizeForOutput(auditEvent),
                cancellationToken);
        }
        catch (Exception)
        {
            await error.WriteLineAsync(
                "Warning: mail suppression change could not be recorded in admin audit events.");
        }
    }

    private async Task<bool> CanUseSuppressionsTableAsync(CancellationToken cancellationToken)
    {
        if (!await connections.CanConnectToMigratedSchemaAsync(cancellationToken))
            return false;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = 'mail_suppressions'
            LIMIT 1;
            """;
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static bool TryParseOptions(
        IReadOnlyList<string> args,
        out Guid tenantId,
        out string recipient,
        out string? error)
    {
        tenantId = default;
        recipient = string.Empty;
        var foundTenantId = false;
        var foundRecipient = false;

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
                case "--tenant-id":
                    if (!Guid.TryParse(value, out tenantId))
                    {
                        error = "--tenant-id must be a UUID.";
                        return false;
                    }

                    foundTenantId = true;
                    break;

                case "--recipient":
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        error = "--recipient must be a non-empty email address.";
                        return false;
                    }

                    recipient = value;
                    foundRecipient = true;
                    break;

                default:
                    error = $"Unknown option: {option}.";
                    return false;
            }
        }

        if (!foundTenantId || !foundRecipient)
        {
            error = "--tenant-id and --recipient are required.";
            return false;
        }

        error = null;
        return true;
    }
}