using Amane.Mailer;
using Amane.Mailer.Admin;
using Amane.Mailer.Api;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Json;
using Amane.Mailer.Operations;
using Amane.Mailer.Worker;
using Microsoft.Extensions.Logging.EventLog;

var commandArgs = NormalizeCommandArgs(args);

if (ShouldShowHelp(commandArgs))
{
    await Console.Out.WriteLineAsync("""
    Usage:
      dotnet Amane.Mailer.dll
      dotnet Amane.Mailer.dll healthcheck
      dotnet Amane.Mailer.dll db migrate
      dotnet Amane.Mailer.dll db checkpoint
      dotnet Amane.Mailer.dll db backup <absolute-path>
      dotnet Amane.Mailer.dll db stats [--tenant-id <uuid>] [--queued-stale-minutes <minutes>] [--failure-window-minutes <minutes>] [--stale-processing-minutes <minutes>]
      dotnet Amane.Mailer.dll db request-state --tenant-id <uuid> --source-service <name> --mail-request-id <uuid>
      dotnet Amane.Mailer.dll admin hash-password

    Options:
      -h, --help    Show help.
    """);
    return 0;
}

if (MailerCliHost.IsHealthCheckCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunHealthCheckAsync(cliConfiguration, CancellationToken.None);
}

if (DbMigrateCommand.IsDbMigrateCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunDbMigrateAsync(cliConfiguration, Console.Out, Console.Error, CancellationToken.None);
}

if (DbCheckpointCommand.IsDbCheckpointCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunDbCheckpointAsync(cliConfiguration, Console.Out, Console.Error, CancellationToken.None);
}

if (DbBackupCommand.IsDbBackupCommand(commandArgs))
{
    if (commandArgs.Count < 3)
    {
        await Console.Error.WriteLineAsync("Usage: dotnet Amane.Mailer.dll db backup <absolute-path>");
        return DbBackupCommand.UsageErrorExitCode;
    }

    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunDbBackupAsync(
        cliConfiguration,
        commandArgs[2],
        Console.Out,
        Console.Error,
        CancellationToken.None);
}

if (DbStatsCommand.IsDbStatsCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunDbStatsAsync(
        cliConfiguration,
        commandArgs,
        Console.Out,
        Console.Error,
        CancellationToken.None);
}

if (DbRequestStateCommand.IsDbRequestStateCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunDbRequestStateAsync(
        cliConfiguration,
        commandArgs,
        Console.Out,
        Console.Error,
        CancellationToken.None);
}

if (AdminHashPasswordCommand.IsAdminHashPasswordCommand(commandArgs))
{
    return await MailerCliHost.RunAdminHashPasswordAsync(
        commandArgs,
        Console.In,
        Console.Out,
        Console.Error);
}

var builder = WebApplication.CreateBuilder(args);

if (OperatingSystem.IsWindows())
{
    builder.Logging.AddFilter<EventLogLoggerProvider>(_ => false);
}

builder.Services.AddMailerJsonSerialization();
builder.Services.AddAmaneMailerServices(builder.Configuration);

var app = builder.Build();

_ = app.Services.GetRequiredService<MailerTenantRegistry>();
_ = app.Services.GetRequiredService<MailerOptions>();

app.MapGet("/healthz", () => MailerJsonResults.Health(true));

app.MapGet("/readyz", async (
    SqliteConnectionFactory connections,
    WorkerServiceStatus serviceStatus,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    try
    {
        var canConnect = await connections.CanConnectToMigratedSchemaAsync(cancellationToken);
        if (!canConnect)
            return MailerJsonResults.Ready(false, StatusCodes.Status503ServiceUnavailable);

        var workerEnabled = configuration.GetValue("Mailer:Worker:Enabled", true);
        if (workerEnabled && (!serviceStatus.IsWorkerRunning || !serviceStatus.IsSweepRunning))
            return MailerJsonResults.Ready(false, StatusCodes.Status503ServiceUnavailable);

        return MailerJsonResults.Ready(true);
    }
    catch
    {
        return MailerJsonResults.Ready(false, StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapMailRequestEndpoints();
app.MapAdminIfEnabled();

app.Run();

return 0;

static IReadOnlyList<string> NormalizeCommandArgs(IReadOnlyList<string> args)
{
    if (args.Count >= 2
        && string.Equals(args[0], "dotnet", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path.GetFileName(args[1]), "Amane.Mailer.dll", StringComparison.OrdinalIgnoreCase))
    {
        return args.Skip(2).ToArray();
    }

    return args;
}

static bool ShouldShowHelp(IReadOnlyList<string> args) =>
    args.Any(arg =>
        string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase));

public partial class Program;
