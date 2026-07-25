using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Webhooks;

namespace Amane.Mailer.Operations;

public static class MailerCliHost
{
    public static bool IsHealthCheckCommand(IReadOnlyList<string> args) =>
        args.Count == 1 && string.Equals(args[0], "healthcheck", StringComparison.Ordinal);

    public static IConfiguration BuildCliConfiguration(string[] args) =>
        new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddCommandLine(args.Where(static arg => arg.Contains('=')).ToArray())
            .Build();

    public static IReadOnlyList<string> FilterConfigurationArgs(IReadOnlyList<string> commandArgs) =>
        commandArgs.Where(static arg => !IsInlineConfigurationArg(arg)).ToArray();

    internal static bool IsInlineConfigurationArg(string arg)
    {
        var equalIndex = arg.IndexOf('=');
        if (equalIndex <= 0)
            return false;

        var key = arg[..equalIndex];
        return key.StartsWith("ConnectionStrings:", StringComparison.Ordinal)
            || key.StartsWith("Mailer:", StringComparison.Ordinal);
    }

    /// <summary>
    /// Attaches Ctrl+C cancellation for a CLI command and maps cooperative cancellation
    /// to <see cref="MailerCliCancellation.ExitCode"/> without treating it as a hard error.
    /// </summary>
    public static async Task<int> RunCancellableCliAsync(
        Func<CancellationToken, Task<int>> execute,
        TextWriter error)
    {
        using var cancellation = MailerCliCancellation.Attach();
        return await MapCancellationAsync(execute, error, cancellation.Token);
    }

    /// <summary>
    /// Maps <see cref="OperationCanceledException"/> for the caller's token to the shared
    /// cancellation exit code. Does not replace unrelated exceptions.
    /// </summary>
    internal static async Task<int> MapCancellationAsync(
        Func<CancellationToken, Task<int>> execute,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        try
        {
            return await execute(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Cancelled.");
            return MailerCliCancellation.ExitCode;
        }
    }

    public static Task<int> RunAdminHashPasswordAsync(
        IReadOnlyList<string> commandArgs,
        TextReader input,
        TextWriter output,
        TextWriter error)
    {
        var command = new AdminHashPasswordCommand();
        return command.ExecuteAsync(commandArgs, input, output, error);
    }

    public static async Task<int> RunAdminUserCreateAsync(
        IConfiguration configuration,
        IReadOnlyList<string> commandArgs,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var factory = new SqliteConnectionFactory(configuration);
        var command = new AdminUserCreateCommand(factory, TimeProvider.System);
        return await command.ExecuteAsync(
            FilterConfigurationArgs(commandArgs),
            output,
            error,
            cancellationToken);
    }

    public static async Task<int> RunHealthCheckAsync(
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var factory = new SqliteConnectionFactory(configuration);
            var runner = new SqlMigrationRunner(factory);
            var ok = await runner.IsCurrentSchemaReadyAsync(cancellationToken);
            if (!ok)
                return 1;

            var workerEnabled = MailerWorkerOptions.IsEnabled(configuration);
            if (!workerEnabled)
                return DbMigrateCommand.SuccessExitCode;

            var healthcheckOptions = MailerHealthcheckOptions.Load(configuration);
            var repository = MailRequestRepository.CreateStandalone(factory);
            var heartbeats = await repository.GetHeartbeatsAsync(cancellationToken);
            if (!WorkerHeartbeatFreshness.AreFresh(heartbeats, healthcheckOptions.MaxHeartbeatStaleness))
                return 1;

            return DbMigrateCommand.SuccessExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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

    public static async Task<int> RunDbAdminAuditPurgeAsync(
        IConfiguration configuration,
        IReadOnlyList<string> commandArgs,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var factory = new SqliteConnectionFactory(configuration);
        var command = new DbAdminAuditPurgeCommand(factory, TimeProvider.System);
        return await command.ExecuteAsync(
            FilterConfigurationArgs(commandArgs),
            configuration,
            output,
            error,
            cancellationToken);
    }

    public static Task<int> RunAdminProviderRegisterAcsAsync(IConfiguration configuration, TextWriter error)
    {
        if (!TryResolveAcsAdminDirectories(configuration, error, out var acsDirectory, out var senderDirectory))
        {
            return Task.FromResult(2);
        }

        var command = new AdminProviderRegisterAcsCommand(
            new AdminProviderRegisterAcsConsole(),
            acsDirectory,
            senderDirectory);
        return Task.FromResult(command.Run());
    }

    public static Task<int> RunAdminProviderCheckAcsPreflightAsync(IConfiguration configuration, TextWriter error)
    {
        if (!TryResolveAcsAdminDirectories(configuration, error, out var acsDirectory, out var senderDirectory))
        {
            return Task.FromResult(2);
        }

        var command = new AdminProviderRegisterAcsCommand(
            new AdminProviderRegisterAcsConsole(),
            acsDirectory,
            senderDirectory);
        return Task.FromResult(command.RunPreflightOnly());
    }

    private static bool TryResolveAcsAdminDirectories(
        IConfiguration configuration,
        TextWriter error,
        out string acsDirectory,
        out string senderDirectory)
    {
        acsDirectory = configuration["MAILER_ACS_SECRET_DIRECTORY"] ?? string.Empty;
        senderDirectory = configuration["MAILER_PLATFORM_SENDER_DIRECTORY"] ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(acsDirectory) && !string.IsNullOrWhiteSpace(senderDirectory))
        {
            return true;
        }

        error.WriteLine("MAILER_ACS_SECRET_DIRECTORY and MAILER_PLATFORM_SENDER_DIRECTORY must both be set.");
        return false;
    }
}
