using System.Text.Json;
using Amane.Mailer.Operations;
using Amane.Mailer.Setup.Assistant;

namespace Amane.Mailer.Setup.NonInteractive;

/// <summary>
/// CLI: <c>setup apply --config &lt;absolute-path&gt; --non-interactive</c> (issue #453 / ADR 0021 D-10).
/// Global parse failures emit usage on stderr only; recognized invocations always emit canonical JSON on stdout.
/// </summary>
public static class SetupApplyNonInteractiveCommand
{
    public const int SuccessExitCode = 0;
    public const int FailureExitCode = 1;
    public const int UsageErrorExitCode = 2;
    public const int CancelledExitCode = MailerCliCancellation.ExitCode;

    public const string UsageLine =
        "Usage: setup apply --config <absolute-path> --non-interactive";

    public static bool IsApplyNonInteractiveCommand(IReadOnlyList<string> args) =>
        args.Count >= 2
        && string.Equals(args[0], "setup", StringComparison.Ordinal)
        && string.Equals(args[1], "apply", StringComparison.Ordinal);

    public static bool TryParseArguments(
        IReadOnlyList<string> args,
        out string? configPath,
        out string? usageError)
    {
        configPath = null;
        usageError = null;
        if (!IsApplyNonInteractiveCommand(args))
        {
            usageError = "Not a setup apply command.";
            return false;
        }

        var configSeen = false;
        var nonInteractiveSeen = false;
        for (var i = 2; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--config", StringComparison.Ordinal))
            {
                if (configSeen)
                {
                    usageError = "Duplicate --config option.";
                    return false;
                }

                if (i + 1 >= args.Count)
                {
                    usageError = "Missing value for --config.";
                    return false;
                }

                i++;
                configPath = args[i];
                configSeen = true;
                continue;
            }

            if (string.Equals(arg, "--non-interactive", StringComparison.Ordinal))
            {
                if (nonInteractiveSeen)
                {
                    usageError = "Duplicate --non-interactive option.";
                    return false;
                }

                nonInteractiveSeen = true;
                continue;
            }

            if (arg.StartsWith('-'))
            {
                usageError = "Unknown argument for setup apply.";
                return false;
            }

            usageError = "Unexpected positional argument for setup apply.";
            return false;
        }

        if (!configSeen || !nonInteractiveSeen)
        {
            usageError = "Missing required --config and --non-interactive.";
            return false;
        }

        return true;
    }

    public static Task<int> ExecuteAsync(
        string configPath,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken) =>
        ExecuteCoreAsync(configPath, output, error, cancellationToken, null, null);

    internal static async Task<int> ExecuteCoreAsync(
        string configPath,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken,
        ISetupAssistantOperations? operations = null,
        ISetupFileSystem? fileSystem = null)
    {
        operations ??= new SetupAssistantOperations();
        fileSystem ??= new HostSetupFileSystem();

        var parse = SetupNonInteractiveInputParser.Parse(fileSystem, configPath);
        if (!parse.Succeeded)
        {
            if (parse.Failure is { } validationFailure)
            {
                await WriteResultAsync(
                    output,
                    SetupNonInteractiveResult.ValidationFailure(
                        validationFailure.Code,
                        validationFailure.Mode,
                        validationFailure.ActionCode));
                await error.WriteLineAsync("setup apply --non-interactive: configuration rejected.");
                return FailureExitCode;
            }

            await WriteResultAsync(
                output,
                SetupNonInteractiveResult.ValidationFailure(parse.FailureCode));
            await error.WriteLineAsync("setup apply --non-interactive: configuration file rejected.");
            return FailureExitCode;
        }

        var input = parse.Input!;
        var wireMode = SetupModeParser.ToWireValue(input.Mode);
        try
        {
            var run = await SetupAssistantMainSetupOrchestrator.RunAsync(
                operations,
                SetupNonInteractiveOrchestratorAdapter.BuildRunRequest(input),
                cancellationToken);
            var result = SetupNonInteractiveOrchestratorAdapter.FromOrchestrator(input.Mode, run);
            await WriteResultAsync(output, result);
            if (!result.Ok)
            {
                await error.WriteLineAsync("setup apply --non-interactive: main setup did not complete successfully.");
            }

            return result.Ok ? SuccessExitCode : FailureExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WriteResultAsync(output, SetupNonInteractiveResult.Cancelled(wireMode));
            await error.WriteLineAsync("setup apply --non-interactive: cancelled.");
            return CancelledExitCode;
        }
        catch
        {
            await WriteResultAsync(
                output,
                SetupNonInteractiveResult.ValidationFailure(
                    AcsSetupResultCode.FailedUnexpected,
                    wireMode));
            await error.WriteLineAsync(
                "setup apply --non-interactive failed: unexpected diagnostic error (details omitted).");
            return FailureExitCode;
        }
    }

    internal static async Task WriteResultAsync(TextWriter output, SetupNonInteractiveResult result)
    {
        var json = JsonSerializer.Serialize(result, SetupNonInteractiveJsonContext.Default.SetupNonInteractiveResult);
        await output.WriteAsync(json);
        if (!json.EndsWith('\n'))
        {
            await output.WriteLineAsync();
        }
    }
}
