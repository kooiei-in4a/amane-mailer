using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Operations;

public sealed class DbCheckpointCommand(SqliteConnectionFactory connections)
{
    public const int SuccessExitCode = 0;
    public const int UsageErrorExitCode = 2;

    public static bool IsDbCheckpointCommand(IReadOnlyList<string> args) =>
        args.Count >= 2
        && string.Equals(args[0], "db", StringComparison.Ordinal)
        && string.Equals(args[1], "checkpoint", StringComparison.Ordinal);

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (args.Count != 2 || !IsDbCheckpointCommand(args))
        {
            await error.WriteLineAsync("Usage: dotnet Amane.Mailer.dll db checkpoint");
            return UsageErrorExitCode;
        }

        await connections.RunWalCheckpointTruncateAsync(cancellationToken);
        await output.WriteLineAsync("WAL checkpoint (TRUNCATE) completed.");
        return SuccessExitCode;
    }
}
