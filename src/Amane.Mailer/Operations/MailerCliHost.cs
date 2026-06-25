using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Operations;

public static class MailerCliHost
{
    private const string WorkerHeartbeatName = "worker";
    private const string SweepHeartbeatName = "sweep";

    public static bool IsHealthCheckCommand(IReadOnlyList<string> args) =>
        args.Count == 1 && string.Equals(args[0], "healthcheck", StringComparison.Ordinal);

    public static IConfiguration BuildCliConfiguration(string[] args) =>
        new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddCommandLine(args.Where(static arg => arg.Contains('=')).ToArray())
            .Build();

    public static Task<int> RunAdminHashPasswordAsync(
        IReadOnlyList<string> commandArgs,
        TextReader input,
        TextWriter output,
        TextWriter error)
    {
        var command = new AdminHashPasswordCommand();
        return command.ExecuteAsync(commandArgs, input, output, error);
    }

    public static async Task<int> RunHealthCheckAsync(
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var factory = new SqliteConnectionFactory(configuration);
            var ok = await factory.CanConnectToMigratedSchemaAsync(cancellationToken);
            if (!ok)
                return 1;

            var workerEnabled = configuration.GetValue("Mailer:Worker:Enabled", true);
            if (!workerEnabled)
                return DbMigrateCommand.SuccessExitCode;

            var healthcheckOptions = MailerHealthcheckOptions.Load(configuration);
            var repository = new MailRequestRepository(factory);
            var heartbeats = await repository.GetHeartbeatsAsync(cancellationToken);

            var workerHeartbeat = heartbeats.FirstOrDefault(h =>
                string.Equals(h.Name, WorkerHeartbeatName, StringComparison.Ordinal));
            var sweepHeartbeat = heartbeats.FirstOrDefault(h =>
                string.Equals(h.Name, SweepHeartbeatName, StringComparison.Ordinal));

            if (workerHeartbeat is null || sweepHeartbeat is null)
                return 1;

            var now = DateTimeOffset.UtcNow;
            if (now - workerHeartbeat.LastHeartbeatAt > healthcheckOptions.MaxHeartbeatStaleness)
                return 1;
            if (now - sweepHeartbeat.LastHeartbeatAt > healthcheckOptions.MaxHeartbeatStaleness)
                return 1;

            return DbMigrateCommand.SuccessExitCode;
        }
        catch
        {
            return 1;
        }
    }

    public static async Task<int> RunDbMigrateAsync(
        IConfiguration configuration,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var factory = new SqliteConnectionFactory(configuration);
        var runner = new SqlMigrationRunner(factory);
        var command = new DbMigrateCommand(runner);
        return await command.ExecuteAsync(["db", "migrate"], output, error, cancellationToken);
    }

    public static async Task<int> RunDbCheckpointAsync(
        IConfiguration configuration,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var factory = new SqliteConnectionFactory(configuration);
        var command = new DbCheckpointCommand(factory);
        return await command.ExecuteAsync(["db", "checkpoint"], output, error, cancellationToken);
    }

    public static async Task<int> RunDbBackupAsync(
        IConfiguration configuration,
        string destinationPath,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var factory = new SqliteConnectionFactory(configuration);
        var command = new DbBackupCommand(factory);
        return await command.ExecuteAsync(["db", "backup", destinationPath], output, error, cancellationToken);
    }

    public static async Task<int> RunDbStatsAsync(
        IConfiguration configuration,
        IReadOnlyList<string> commandArgs,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var factory = new SqliteConnectionFactory(configuration);
        var command = new DbStatsCommand(factory);
        return await command.ExecuteAsync(commandArgs, output, error, cancellationToken);
    }

    public static async Task<int> RunDbRequestStateAsync(
        IConfiguration configuration,
        IReadOnlyList<string> commandArgs,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var factory = new SqliteConnectionFactory(configuration);
        var command = new DbRequestStateCommand(factory);
        return await command.ExecuteAsync(commandArgs, output, error, cancellationToken);
    }
}
