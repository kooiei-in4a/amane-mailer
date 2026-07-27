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
      dotnet Amane.Mailer.dll db suppressions remove --tenant-id <uuid> --recipient <email>
      dotnet Amane.Mailer.dll admin hash-password
      dotnet Amane.Mailer.dll admin user create --username <name> --password-hash <pbkdf2> [--tenant-id <uuid> ...] [--break-glass]
      dotnet Amane.Mailer.dll admin provider register-acs
      dotnet Amane.Mailer.dll admin provider check-acs-preflight
      dotnet Amane.Mailer.dll admin provider test-acs-send
      dotnet Amane.Mailer.dll setup doctor --mode <mode> [--compose-file <path>]

    Setup doctor modes:
      local-mailpit, staging-no-send, staging-verification, production-acs, production-queue

    Options:
      -h, --help    Show help.
    """);
    return 0;
}

if (MailerCliHost.IsHealthCheckCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunCancellableCliAsync(
        ct => MailerCliHost.RunHealthCheckAsync(cliConfiguration, ct),
        Console.Error);
}

if (DbMigrateCommand.IsDbMigrateCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunCancellableCliAsync(
        ct => MailerCliHost.RunDbMigrateAsync(cliConfiguration, Console.Out, Console.Error, ct),
        Console.Error);
}

if (DbCheckpointCommand.IsDbCheckpointCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunCancellableCliAsync(
        ct => MailerCliHost.RunDbCheckpointAsync(cliConfiguration, Console.Out, Console.Error, ct),
        Console.Error);
}

if (DbBackupCommand.IsDbBackupCommand(commandArgs))
{
    if (commandArgs.Count < 3)
    {
        await Console.Error.WriteLineAsync("Usage: dotnet Amane.Mailer.dll db backup <absolute-path>");
        return DbBackupCommand.UsageErrorExitCode;
    }

    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    var destinationPath = commandArgs[2];
    return await MailerCliHost.RunCancellableCliAsync(
        ct => MailerCliHost.RunDbBackupAsync(
            cliConfiguration,
            destinationPath,
            Console.Out,
            Console.Error,
            ct),
        Console.Error);
}

if (DbStatsCommand.IsDbStatsCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunCancellableCliAsync(
        ct => MailerCliHost.RunDbStatsAsync(
            cliConfiguration,
            commandArgs,
            Console.Out,
            Console.Error,
            ct),
        Console.Error);
}

if (DbRequestStateCommand.IsDbRequestStateCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunCancellableCliAsync(
        ct => MailerCliHost.RunDbRequestStateAsync(
            cliConfiguration,
            commandArgs,
            Console.Out,
            Console.Error,
            ct),
        Console.Error);
}

if (DbAdminAuditPurgeCommand.IsDbAdminAuditPurgeCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunCancellableCliAsync(
        ct => MailerCliHost.RunDbAdminAuditPurgeAsync(
            cliConfiguration,
            commandArgs,
            Console.Out,
            Console.Error,
            ct),
        Console.Error);
}

if (DbSuppressionsRemoveCommand.IsDbSuppressionsRemoveCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunCancellableCliAsync(
        ct => MailerCliHost.RunDbSuppressionsRemoveAsync(
            cliConfiguration,
            commandArgs,
            Console.Out,
            Console.Error,
            ct),
        Console.Error);
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
    return await MailerCliHost.RunCancellableCliAsync(
        ct => MailerCliHost.RunAdminUserCreateAsync(
            cliConfiguration,
            commandArgs,
            Console.Out,
            Console.Error,
            ct),
        Console.Error);
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

if (AdminProviderTestAcsSendCommand.IsTestAcsSendCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunCancellableCliAsync(
        ct => MailerCliHost.RunAdminProviderTestAcsSendAsync(cliConfiguration, ct),
        Console.Error);
}

if (SetupDoctorCommand.IsSetupDoctorCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunCancellableCliAsync(
        ct => MailerCliHost.RunSetupDoctorAsync(cliConfiguration, commandArgs, Console.Out, Console.Error, ct),
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

// Single startup path: resolve every AddStartupValidatedSingleton registration so Load/Validate
// fail-fast (Worker/Admin enabled gates stay inside each options type).
app.Services.GetRequiredService<MailerStartupValidator>().Validate();

app.MapGet("/healthz", () => MailerJsonResults.Health(true));

app.MapGet("/readyz", async (
    SqlMigrationRunner migrationRunner,
    WorkerServiceStatus serviceStatus,
    MailRequestRepository repository,
    MailerHealthcheckOptions healthcheckOptions,
    MailerReadinessEvaluator readinessEvaluator,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var workerEnabled = MailerWorkerOptions.IsEnabled(configuration);
    var result = await readinessEvaluator.EvaluateAsync(
        migrationRunner,
        serviceStatus,
        repository,
        healthcheckOptions,
        workerEnabled,
        cancellationToken);

    return result.IsReady
        ? MailerJsonResults.Ready(true)
        : MailerJsonResults.Ready(false, StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/metrics", MailerMetricsEndpoint.HandleAsync);

app.MapMailRequestEndpoints();
await app.EnsureAdminReadyAsync(app.Lifetime.ApplicationStopping);
app.MapAdminIfEnabled();

await app.RunAsync();

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
