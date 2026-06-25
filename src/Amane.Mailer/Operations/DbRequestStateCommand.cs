using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Operations;

public sealed class DbRequestStateCommand(SqliteConnectionFactory connections)
{
    public const int SuccessExitCode = 0;
    public const int UnavailableExitCode = 1;
    public const int UsageErrorExitCode = 2;

    public static bool IsDbRequestStateCommand(IReadOnlyList<string> args) =>
        args.Count >= 2
        && string.Equals(args[0], "db", StringComparison.Ordinal)
        && string.Equals(args[1], "request-state", StringComparison.Ordinal);

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        string? parseError = null;
        if (!IsDbRequestStateCommand(args) || !TryParseOptions(args, out var options, out parseError))
        {
            if (!string.IsNullOrWhiteSpace(parseError))
            {
                await error.WriteLineAsync(parseError);
            }

            await error.WriteLineAsync("Usage: dotnet Amane.Mailer.dll db request-state --tenant-id <uuid> --source-service <name> --mail-request-id <uuid>");
            return UsageErrorExitCode;
        }

        if (!await CanReadMigratedSchemaAsync(cancellationToken))
        {
            await error.WriteLineAsync("Mailer database schema is not migrated.");
            return UnavailableExitCode;
        }

        var state = await LoadRequestStateAsync(options, cancellationToken);
        await WriteStateAsync(options, state, output);
        return SuccessExitCode;
    }

    private async Task<RequestState?> LoadRequestStateAsync(
        DbRequestStateOptions options,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                mr.id,
                mr.status,
                mr.attempt_count,
                mr.purpose,
                (SELECT COUNT(*) FROM mail_attempts ma WHERE ma.request_id = mr.id) AS attempt_rows,
                COALESCE((SELECT ma.provider FROM mail_attempts ma WHERE ma.request_id = mr.id ORDER BY ma.id DESC LIMIT 1), '') AS last_provider,
                COALESCE((SELECT ma.status FROM mail_attempts ma WHERE ma.request_id = mr.id ORDER BY ma.id DESC LIMIT 1), -1) AS last_attempt_status,
                COALESCE((SELECT CASE WHEN ma.provider_message_id IS NULL OR ma.provider_message_id = '' THEN 0 ELSE 1 END FROM mail_attempts ma WHERE ma.request_id = mr.id ORDER BY ma.id DESC LIMIT 1), 0) AS provider_message_id_present,
                COALESCE((SELECT ma.error_code FROM mail_attempts ma WHERE ma.request_id = mr.id ORDER BY ma.id DESC LIMIT 1), '') AS last_error_code
            FROM mail_requests mr
            WHERE mr.tenant_id = @TenantId
              AND mr.source_service = @SourceService
              AND mr.mail_request_id = @MailRequestId
            LIMIT 1;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@TenantId", options.TenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", options.SourceService);
        command.Parameters.AddWithValue("@MailRequestId", options.MailRequestId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var status = (MailRequestState)reader.GetInt32(1);
        var lastAttemptStatusCode = reader.GetInt32(6);

        return new RequestState(
            Id: reader.GetString(0),
            Status: status,
            AttemptCount: reader.GetInt32(2),
            Purpose: reader.GetString(3),
            AttemptRows: reader.GetInt64(4),
            LastProvider: reader.GetString(5),
            LastAttemptStatusCode: lastAttemptStatusCode,
            LastAttemptStatus: lastAttemptStatusCode >= 0
                ? StatusText((MailRequestState)lastAttemptStatusCode)
                : string.Empty,
            LastAttemptStatusCodeText: lastAttemptStatusCode >= 0
                ? lastAttemptStatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty,
            ProviderMessageIdPresent: reader.GetInt32(7) == 1,
            LastErrorCode: reader.GetString(8));
    }

    private async Task<bool> CanReadMigratedSchemaAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await connections.CanConnectToMigratedSchemaAsync(cancellationToken);
        }
        catch
        {
            return false;
        }
    }

    private static async Task WriteStateAsync(
        DbRequestStateOptions options,
        RequestState? state,
        TextWriter output)
    {
        await output.WriteLineAsync($"tenant_id={options.TenantId:D}");
        await output.WriteLineAsync($"source_service={options.SourceService}");
        await output.WriteLineAsync($"mail_request_id={options.MailRequestId:D}");

        if (state is null)
        {
            await output.WriteLineAsync("found=false");
            return;
        }

        await output.WriteLineAsync("found=true");
        await output.WriteLineAsync($"id={state.Id}");
        await output.WriteLineAsync($"purpose={state.Purpose}");
        await output.WriteLineAsync($"status={StatusText(state.Status)}");
        await output.WriteLineAsync($"status_code={(int)state.Status}");
        await output.WriteLineAsync($"attempt_count={state.AttemptCount}");
        await output.WriteLineAsync($"attempt_rows={state.AttemptRows}");
        await output.WriteLineAsync($"last_provider={state.LastProvider}");
        await output.WriteLineAsync($"last_attempt_status={state.LastAttemptStatus}");
        await output.WriteLineAsync($"last_attempt_status_code={state.LastAttemptStatusCodeText}");
        await output.WriteLineAsync($"provider_message_id_present={state.ProviderMessageIdPresent.ToString().ToLowerInvariant()}");
        await output.WriteLineAsync($"last_error_code={state.LastErrorCode}");
    }

    private static bool TryParseOptions(
        IReadOnlyList<string> args,
        out DbRequestStateOptions options,
        out string? error)
    {
        Guid? tenantId = null;
        string? sourceService = null;
        Guid? mailRequestId = null;

        for (var index = 2; index < args.Count; index++)
        {
            var option = args[index];
            if (index + 1 >= args.Count)
            {
                options = default;
                error = $"Missing value for {option}.";
                return false;
            }

            var value = args[++index];
            switch (option)
            {
                case "--tenant-id":
                    if (!Guid.TryParse(value, out var parsedTenantId))
                    {
                        options = default;
                        error = "--tenant-id must be a UUID.";
                        return false;
                    }

                    tenantId = parsedTenantId;
                    break;

                case "--source-service":
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        options = default;
                        error = "--source-service must not be empty.";
                        return false;
                    }

                    sourceService = value;
                    break;

                case "--mail-request-id":
                    if (!Guid.TryParse(value, out var parsedMailRequestId))
                    {
                        options = default;
                        error = "--mail-request-id must be a UUID.";
                        return false;
                    }

                    mailRequestId = parsedMailRequestId;
                    break;

                default:
                    options = default;
                    error = $"Unknown option: {option}.";
                    return false;
            }
        }

        if (tenantId is null || string.IsNullOrWhiteSpace(sourceService) || mailRequestId is null)
        {
            options = default;
            error = "--tenant-id, --source-service, and --mail-request-id are required.";
            return false;
        }

        options = new DbRequestStateOptions(tenantId.Value, sourceService, mailRequestId.Value);
        error = null;
        return true;
    }

    private static string StatusText(MailRequestState status) =>
        status switch
        {
            MailRequestState.Queued => "queued",
            MailRequestState.Processing => "processing",
            MailRequestState.Delivered => "delivered",
            MailRequestState.Failed => "failed",
            MailRequestState.DeadLettered => "dead_lettered",
            _ => ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    private readonly record struct DbRequestStateOptions(
        Guid TenantId,
        string SourceService,
        Guid MailRequestId);

    private sealed record RequestState(
        string Id,
        MailRequestState Status,
        int AttemptCount,
        string Purpose,
        long AttemptRows,
        string LastProvider,
        int LastAttemptStatusCode,
        string LastAttemptStatus,
        string LastAttemptStatusCodeText,
        bool ProviderMessageIdPresent,
        string LastErrorCode);
}
