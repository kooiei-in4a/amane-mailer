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
      dotnet Amane.Mailer.dll db admin-audit purge --older-than-days <days>
      dotnet Amane.Mailer.dll admin hash-password
      dotnet Amane.Mailer.dll admin user create --username <name> --password-hash <pbkdf2> [--tenant-id <uuid> ...] [--break-glass]
      dotnet Amane.Mailer.dll admin provider register-acs
      dotnet Amane.Mailer.dll admin provider check-acs-preflight

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

if (DbAdminAuditPurgeCommand.IsDbAdminAuditPurgeCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunDbAdminAuditPurgeAsync(
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

var adminUserCommandArgs = MailerCliHost.FilterConfigurationArgs(commandArgs);
if (AdminUserCreateCommand.IsAdminUserCreateCommand(adminUserCommandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunAdminUserCreateAsync(
        cliConfiguration,
        commandArgs,
        Console.Out,
        Console.Error,
        CancellationToken.None);
}

if (AdminProviderRegisterAcsCommand.IsRegisterAcsCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunAdminProviderRegisterAcsAsync(cliConfiguration, Console.Error);
}

if (AdminProviderRegisterAcsCommand.IsCheckAcsPreflightCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunAdminProviderCheckAcsPreflightAsync(cliConfiguration, Console.Error);
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
_ = app.Services.GetRequiredService<MailerMetricsOptions>();
// Fail fast on operational numeric misconfiguration (same Load rules with Worker disabled;
// cross-field Validate for lease/healthcheck remains Worker-enabled only).
_ = app.Services.GetRequiredService<MailerWorkerOptions>();
_ = app.Services.GetRequiredService<MailerWebhookOptions>();
_ = app.Services.GetRequiredService<MailerSweepOptions>();
_ = app.Services.GetRequiredService<MailerRetentionOptions>();
_ = app.Services.GetRequiredService<MailerAdminAuditRetentionOptions>();
_ = app.Services.GetRequiredService<MailerHealthcheckOptions>();

app.MapGet("/healthz", () => MailerJsonResults.Health(true));

app.MapGet("/readyz", async (
    SqliteConnectionFactory connections,
    SqlMigrationRunner migrationRunner,
    WorkerServiceStatus serviceStatus,
    MailRequestRepository repository,
    MailerHealthcheckOptions healthcheckOptions,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    try
    {
        var canConnect = await migrationRunner.IsCurrentSchemaReadyAsync(cancellationToken);
        if (!canConnect)
            return MailerJsonResults.Ready(false, StatusCodes.Status503ServiceUnavailable);

        var workerEnabled = configuration.GetValue("Mailer:Worker:Enabled", true);
        if (workerEnabled)
        {
            if (!serviceStatus.IsWorkerRunning || !serviceStatus.IsSweepRunning)
                return MailerJsonResults.Ready(false, StatusCodes.Status503ServiceUnavailable);

            var heartbeats = await repository.GetHeartbeatsAsync(cancellationToken);
            if (!WorkerHeartbeatFreshness.AreFresh(heartbeats, healthcheckOptions.MaxHeartbeatStaleness))
                return MailerJsonResults.Ready(false, StatusCodes.Status503ServiceUnavailable);
        }

        return MailerJsonResults.Ready(true);
    }
    catch
    {
        return MailerJsonResults.Ready(false, StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/metrics", MailerMetricsEndpoint.HandleAsync);

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
