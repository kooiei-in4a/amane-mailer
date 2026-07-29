using Amane.Mailer.Setup;
using System.Text.Json;

namespace Amane.Mailer.Operations;

public sealed class DbMigrateCommand(SqlMigrationRunner migrationRunner)
{
    public const int SuccessExitCode = 0;
    public const int UsageErrorExitCode = 2;
    public const string Usage = "Usage: dotnet Amane.Mailer.dll db migrate [--status --format json]";

    public static bool IsDbMigrateCommand(IReadOnlyList<string> args) =>
        args.Count >= 2
        && string.Equals(args[0], "db", StringComparison.Ordinal)
        && string.Equals(args[1], "migrate", StringComparison.Ordinal);

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!IsDbMigrateCommand(args) || !TryParseMode(args, out var statusOnly))
        {
            await error.WriteLineAsync(Usage);
            return UsageErrorExitCode;
        }

        if (statusOnly)
        {
            return await WriteStatusAsync(output, cancellationToken);
        }

        var applied = await migrationRunner.ApplyPendingAsync(cancellationToken);

        await output.WriteLineAsync(applied.Count == 0
            ? "Database is up to date."
            : $"Applied database migrations: {applied.Count} ({string.Join(", ", applied)})");

        return SuccessExitCode;
    }

    /// <summary>
    /// Read-only classification for the Managed apply engine. Emits exactly one JSON object and
    /// never writes to the database, so it is safe to run before an upgrade decision is made.
    /// </summary>
    private async Task<int> WriteStatusAsync(TextWriter output, CancellationToken cancellationToken)
    {
        var classification = await migrationRunner.ClassifySchemaAsync(cancellationToken);
        var document = new SetupMigrationStatusDocument
        {
            SchemaVersion = SetupMigrationStatusDocument.CurrentSchemaVersion,
            Classification = classification,
        };

        var json = JsonSerializer.Serialize(
            document,
            SetupApplyJsonContext.Default.SetupMigrationStatusDocument);
        await output.WriteLineAsync(json);
        return SuccessExitCode;
    }

    /// <summary>
    /// Accepts the historical bare <c>db migrate</c> and the read-only
    /// <c>db migrate --status --format json</c>. JSON is the only supported status format so
    /// machine callers cannot depend on an unstable text shape.
    /// </summary>
    private static bool TryParseMode(IReadOnlyList<string> args, out bool statusOnly)
    {
        statusOnly = false;
        var formatSeen = false;

        for (var i = 2; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--status", StringComparison.Ordinal))
            {
                statusOnly = true;
                continue;
            }

            if (string.Equals(arg, "--format", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Count || !string.Equals(args[i + 1], "json", StringComparison.Ordinal))
                {
                    return false;
                }

                i++;
                formatSeen = true;
                continue;
            }

            return false;
        }

        // --format is meaningful only for --status, and --status must be machine readable.
        return statusOnly == formatSeen;
    }
}
