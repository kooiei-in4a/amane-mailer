using Amane.Mailer;
using Amane.Mailer.Admin;
using Amane.Mailer.Api;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Json;
using Amane.Mailer.Operations;
using Amane.Mailer.Operations.EventGridConfigCheck;
using Amane.Mailer.Operations.VerifyDeliveryReport;
using Amane.Mailer.Setup;
using Amane.Mailer.Worker;
using Microsoft.AspNetCore.Builder;
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
      dotnet Amane.Mailer.dll admin reset-password
      dotnet Amane.Mailer.dll admin user create --username <name> --password-hash <pbkdf2> [--tenant-id <uuid> ...] [--break-glass]
      dotnet Amane.Mailer.dll admin user capability <grant|revoke> --username <name> --capability bcc_recipient_reveal
      dotnet Amane.Mailer.dll admin provider register-acs
      dotnet Amane.Mailer.dll admin provider check-acs-preflight
      dotnet Amane.Mailer.dll admin provider test-acs-send
      dotnet Amane.Mailer.dll setup assistant [--port <1-65535>] [--no-browser] [--terminal]
      dotnet Amane.Mailer.dll setup bootstrap show
      dotnet Amane.Mailer.dll setup assistant-self-check
      dotnet Amane.Mailer.dll setup doctor --mode <mode> [--compose-file <path>]
      dotnet Amane.Mailer.dll setup apply --config <absolute-path> --non-interactive
      dotnet Amane.Mailer.dll setup inspect-effective --format json
      dotnet Amane.Mailer.dll setup core-self-check
      dotnet Amane.Mailer.dll setup host-docker-self-check
      dotnet Amane.Mailer.dll setup check-event-grid --subscription <id-or-name> --resource-group <rg> (--acs-name <name> | --acs-resource-id <id>) --event-subscription <name> --storage-account <name> --queue-name <name> --environment <dev|staging|production>
      dotnet Amane.Mailer.dll setup verify-delivery-report

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
        ct => MailerCliHost.RunDbMigrateAsync(cliConfiguration, commandArgs, Console.Out, Console.Error, ct),
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

if (AdminResetPasswordCommand.IsAdminResetPasswordCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunCancellableCliAsync(
        ct => MailerCliHost.RunAdminResetPasswordAsync(
            cliConfiguration,
            commandArgs,
            Console.In,
            Console.Out,
            Console.Error,
            ct),
        Console.Error);
}

if (BootstrapShowCommand.IsBootstrapShowCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunCancellableCliAsync(
        ct => MailerCliHost.RunBootstrapShowAsync(
            cliConfiguration,
            Console.Out,
            Console.Error,
            ct),
        Console.Error);
}

var adminUserCommandArgs = MailerCliHost.FilterConfigurationArgs(commandArgs);
if (AdminUserCapabilityCommand.IsAdminUserCapabilityCommand(adminUserCommandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunCancellableCliAsync(
        ct => MailerCliHost.RunAdminUserCapabilityAsync(
            cliConfiguration,
            commandArgs,
            Console.Out,
            Console.Error,
            ct),
        Console.Error);
}

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


// The assistant runs an isolated loopback web host. It never starts the normal Mailer runtime,
// and the runtime never gains a setup route.
if (Amane.Mailer.Setup.Assistant.SetupAssistantCommand.IsAssistantCommand(commandArgs))
{
    return await MailerCliHost.RunCancellableCliAsync(
        ct => Amane.Mailer.Setup.Assistant.SetupAssistantCommand.ExecuteAsync(
            commandArgs,
            Console.Out,
            Console.Error,
            ct),
        Console.Error);
}

if (Amane.Mailer.Setup.Assistant.SetupAssistantSelfCheckCommand.IsSelfCheckCommand(commandArgs))
{
    return await Amane.Mailer.Setup.Assistant.SetupAssistantSelfCheckCommand.ExecuteAsync(
        Console.Out,
        Console.Error);
}

if (Amane.Mailer.Setup.SetupCoreSelfCheckCommand.IsSelfCheckCommand(commandArgs))
{
    return await Amane.Mailer.Setup.SetupCoreSelfCheckCommand.ExecuteAsync(Console.Out, Console.Error);
}

if (Amane.Mailer.Setup.SetupHostDockerSelfCheckCommand.IsSelfCheckCommand(commandArgs))
{
    return await Amane.Mailer.Setup.SetupHostDockerSelfCheckCommand.ExecuteAsync(Console.Out, Console.Error);
}

if (Amane.Mailer.Setup.NonInteractive.SetupApplyNonInteractiveCommand.IsApplyNonInteractiveCommand(commandArgs))
{
    if (!Amane.Mailer.Setup.NonInteractive.SetupApplyNonInteractiveCommand.TryParseArguments(
            commandArgs,
            out var configPath,
            out var usageError))
    {
        await Console.Error.WriteLineAsync(usageError ?? "Invalid setup apply arguments.");
        await Console.Error.WriteLineAsync(
            Amane.Mailer.Setup.NonInteractive.SetupApplyNonInteractiveCommand.UsageLine);
        return Amane.Mailer.Setup.NonInteractive.SetupApplyNonInteractiveCommand.UsageErrorExitCode;
    }

    return await MailerCliHost.RunCancellableCliAsync(
        ct => Amane.Mailer.Setup.NonInteractive.SetupApplyNonInteractiveCommand.ExecuteAsync(
            configPath!,
            Console.Out,
            Console.Error,
            ct),
        Console.Error);
}

if (Amane.Mailer.Setup.SetupInspectEffectiveCommand.IsInspectEffectiveCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunCancellableCliAsync(
        ct => MailerCliHost.RunSetupInspectEffectiveAsync(
            cliConfiguration,
            commandArgs,
            Console.Out,
            Console.Error,
            ct),
        Console.Error);
}

if (SetupDoctorCommand.IsSetupDoctorCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunCancellableCliAsync(
        ct => MailerCliHost.RunSetupDoctorAsync(cliConfiguration, commandArgs, Console.Out, Console.Error, ct),
        Console.Error);
}

if (VerifyDeliveryReportCommand.IsVerifyDeliveryReportCommand(commandArgs))
{
    var cliConfiguration = MailerCliHost.BuildCliConfiguration(args);
    return await MailerCliHost.RunCancellableCliAsync(
        ct => MailerCliHost.RunSetupVerifyDeliveryReportAsync(cliConfiguration, ct),
        Console.Error);
}

if (EventGridConfigCheckCommand.IsEventGridConfigCheckCommand(commandArgs))
{
    return await MailerCliHost.RunCancellableCliAsync(
        ct => MailerCliHost.RunSetupCheckEventGridAsync(commandArgs, Console.Out, Console.Error, ct),
        Console.Error);
}

var builder = WebApplication.CreateBuilder(args);
var instanceState = await InstanceRuntimeStateProbe.ReadAsync(builder.Configuration);

if (OperatingSystem.IsWindows())
{
    builder.Logging.AddFilter<EventLogLoggerProvider>(_ => false);
}

builder.Services.AddMailerJsonSerialization();
builder.Services.AddAmaneMailerServices(builder.Configuration, instanceState);
ForwardedHeadersStartup.ConfigureServices(builder.Services, builder.Configuration);
var allowedHosts = builder.Configuration["AllowedHosts"];
if (!string.IsNullOrWhiteSpace(allowedHosts))
{
    builder.Services.AddHostFiltering(options =>
    {
        options.AllowedHosts = allowedHosts
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        options.AllowEmptyHosts = false;
        options.IncludeFailureMessage = false;
    });
}

var app = builder.Build();

if (!string.IsNullOrWhiteSpace(allowedHosts))
{
    app.UseHostFiltering();
}

// Single startup path: resolve every AddStartupValidatedSingleton registration so Load/Validate
// fail-fast (Worker/Admin enabled gates stay inside each options type).
app.Services.GetRequiredService<MailerStartupValidator>().Validate();

// Before Admin (Secure / antiforgery) so X-Forwarded-Proto from an approved TLS-terminating
// reverse proxy makes Request.IsHttps true when ASPNETCORE_FORWARDEDHEADERS_ENABLED=true.
ForwardedHeadersStartup.UseIfEnabled(app);

app.MapGet("/healthz", () => MailerJsonResults.Health(true));

if (instanceState.IsUninitialized)
{
    // Token generation is intentionally part of the uninitialized startup path only. Once the
    // singleton gate is initialized, a stale token file is neither read nor recreated.
    app.Services.GetRequiredService<BootstrapTokenStore>().EnsureExists();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapGet("/readyz", () => MailerJsonResults.Ready(
        false,
        StatusCodes.Status503ServiceUnavailable,
        MailerReadinessReasons.Uninitialized));
    FirstRunSetupEndpoints.Map(app);
    await app.RunAsync();
    return 0;
}

app.MapGet("/readyz", async (
    InstanceRuntimeState runtimeState,
    SqlMigrationRunner migrationRunner,
    WorkerServiceStatus serviceStatus,
    MailRequestRepository repository,
    MailerHealthcheckOptions healthcheckOptions,
    MailerReadinessEvaluator readinessEvaluator,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    if (runtimeState.IsInitialized
        && string.Equals(runtimeState.ProviderType, "acs", StringComparison.Ordinal)
        && (string.IsNullOrWhiteSpace(runtimeState.ProviderSecretRef)
            || !FirstRunSetupStorage.TryReadValidAcsSecret(runtimeState.ProviderSecretRef, out _)))
    {
        return MailerJsonResults.Ready(
            false,
            StatusCodes.Status503ServiceUnavailable,
            MailerReadinessReasons.ProviderSecretMissing);
    }

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
