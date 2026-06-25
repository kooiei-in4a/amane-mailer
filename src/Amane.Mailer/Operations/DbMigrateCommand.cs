namespace Amane.Mailer.Operations;

public sealed class DbMigrateCommand(SqlMigrationRunner migrationRunner)
{
    public const int SuccessExitCode = 0;
    public const int UsageErrorExitCode = 2;

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
        if (args.Count != 2 || !IsDbMigrateCommand(args))
        {
            await error.WriteLineAsync("Usage: dotnet Amane.Mailer.dll db migrate");
            return UsageErrorExitCode;
        }

        var applied = await migrationRunner.ApplyPendingAsync(cancellationToken);

        await output.WriteLineAsync(applied.Count == 0
            ? "Database is up to date."
            : $"Applied database migrations: {applied.Count} ({string.Join(", ", applied)})");

        return SuccessExitCode;
    }
}
