using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Operations;

public sealed class DbBackupCommand(SqliteConnectionFactory connections)
{
    public const int SuccessExitCode = 0;
    public const int UsageErrorExitCode = 2;

    public static bool IsDbBackupCommand(IReadOnlyList<string> args) =>
        args.Count >= 2
        && string.Equals(args[0], "db", StringComparison.Ordinal)
        && string.Equals(args[1], "backup", StringComparison.Ordinal);

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (args.Count != 3 || !IsDbBackupCommand(args))
        {
            await error.WriteLineAsync("Usage: dotnet Amane.Mailer.dll db backup <absolute-path>");
            return UsageErrorExitCode;
        }

        var destinationPath = args[2];
        if (!Path.IsPathRooted(destinationPath))
        {
            await error.WriteLineAsync("Backup destination must be an absolute path.");
            return UsageErrorExitCode;
        }

        if (connections.IsConfiguredDatabasePath(destinationPath))
        {
            await error.WriteLineAsync("Backup destination must not be the active mailer database.");
            return UsageErrorExitCode;
        }

        await connections.BackupToAsync(destinationPath, cancellationToken);
        await output.WriteLineAsync($"Database backup written to {destinationPath}");
        return SuccessExitCode;
    }
}
