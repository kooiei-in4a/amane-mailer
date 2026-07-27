namespace Amane.Mailer.Operations.EventGridConfigCheck;

/// <summary>
/// CLI entry for read-only ACS Event Grid to Storage Queue configuration checks (#427).
/// </summary>
public sealed class EventGridConfigCheckCommand
{
    public const int SuccessExitCode = 0;
    public const int FailureExitCode = 1;
    public const int UsageErrorExitCode = 2;

    public const string Usage =
        "setup check-event-grid --subscription <id-or-name> --resource-group <rg> " +
        "(--acs-name <name> | --acs-resource-id <id>) --event-subscription <name> " +
        "--storage-account <name> --queue-name <name> --environment <dev|staging|production>";

    private readonly IAzureCliRunner _runner;
    private readonly EventGridConfigCheckOptions _options;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly SetupDoctorReport _report = new();

    public EventGridConfigCheckCommand(
        IAzureCliRunner runner,
        EventGridConfigCheckOptions options,
        TextWriter output,
        TextWriter error)
    {
        _runner = runner;
        _options = options;
        _output = output;
        _error = error;
    }

    public static bool IsEventGridConfigCheckCommand(IReadOnlyList<string> args) =>
        args.Count >= 2
        && string.Equals(args[0], "setup", StringComparison.Ordinal)
        && string.Equals(args[1], "check-event-grid", StringComparison.Ordinal);

    public static bool TryParseArguments(
        IReadOnlyList<string> args,
        out EventGridConfigCheckOptions? options,
        out string? usageError)
    {
        options = null;
        usageError = null;

        string? subscription = null;
        string? resourceGroup = null;
        string? acsName = null;
        string? acsResourceId = null;
        string? eventSubscription = null;
        string? storageAccount = null;
        string? queueName = null;
        string? environmentRaw = null;

        var index = 2;
        while (index < args.Count)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                usageError = "Unknown argument.";
                return false;
            }

            index++;
            if (index >= args.Count)
            {
                usageError = $"{token} requires a value.";
                return false;
            }

            var value = args[index];
            index++;

            if (IsSecretLikeFlag(token) || IsSecretLikeValue(value))
            {
                usageError =
                    "ACS keys, Storage keys, connection strings, tokens, and email addresses must not be supplied.";
                return false;
            }

            switch (token)
            {
                case "--subscription":
                    subscription = value;
                    break;
                case "--resource-group":
                    resourceGroup = value;
                    break;
                case "--acs-name":
                    acsName = value;
                    break;
                case "--acs-resource-id":
                    acsResourceId = value;
                    break;
                case "--event-subscription":
                    eventSubscription = value;
                    break;
                case "--storage-account":
                    storageAccount = value;
                    break;
                case "--queue-name":
                    queueName = value;
                    break;
                case "--environment":
                    environmentRaw = value;
                    break;
                default:
                    usageError = "Unknown argument.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(subscription)
            || string.IsNullOrWhiteSpace(resourceGroup)
            || string.IsNullOrWhiteSpace(eventSubscription)
            || string.IsNullOrWhiteSpace(storageAccount)
            || string.IsNullOrWhiteSpace(queueName)
            || string.IsNullOrWhiteSpace(environmentRaw))
        {
            usageError = "Missing required arguments.";
            return false;
        }

        var hasAcsName = !string.IsNullOrWhiteSpace(acsName);
        var hasAcsId = !string.IsNullOrWhiteSpace(acsResourceId);
        if (hasAcsName == hasAcsId)
        {
            usageError = "Specify exactly one of --acs-name or --acs-resource-id.";
            return false;
        }

        if (!EventGridConfigEnvironmentParser.TryParse(environmentRaw, out var environment))
        {
            usageError = $"Unknown --environment value. Expected one of: {EventGridConfigEnvironmentParser.UsageHint}.";
            return false;
        }

        if (LooksLikeConnectionString(subscription)
            || LooksLikeConnectionString(resourceGroup)
            || LooksLikeConnectionString(acsName)
            || LooksLikeConnectionString(acsResourceId)
            || LooksLikeConnectionString(storageAccount)
            || LooksLikeConnectionString(queueName))
        {
            usageError =
                "ACS keys, Storage keys, connection strings, tokens, and email addresses must not be supplied.";
            return false;
        }

        options = new EventGridConfigCheckOptions
        {
            Subscription = subscription.Trim(),
            ResourceGroup = resourceGroup.Trim(),
            AcsName = hasAcsName ? acsName!.Trim() : null,
            AcsResourceId = hasAcsId ? acsResourceId!.Trim() : null,
            EventSubscriptionName = eventSubscription.Trim(),
            StorageAccountName = storageAccount.Trim(),
            QueueName = queueName.Trim(),
            Environment = environment,
        };
        return true;
    }

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            var checker = new EventGridConfigChecker(_runner, _options, _report);
            await checker.ExecuteAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            await _error.WriteLineAsync(ex.Message);
            return UsageErrorExitCode;
        }
        catch
        {
            _report.AddFail(
                "check_unexpected",
                "Event Grid configuration check encountered an unexpected error (details omitted).");
            await _error.WriteLineAsync(
                "setup check-event-grid failed: unexpected diagnostic error (details omitted).");
        }

        await WriteReportAsync(cancellationToken);
        return _report.HasFailure ? FailureExitCode : SuccessExitCode;
    }

    private async Task WriteReportAsync(CancellationToken cancellationToken)
    {
        foreach (var check in _report.Checks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var codeLabel = check.Code switch
            {
                SetupDoctorResultCode.Pass => "PASS",
                SetupDoctorResultCode.Fail => "FAIL",
                SetupDoctorResultCode.Warn => "WARN",
                SetupDoctorResultCode.Action => "ACTION",
                _ => check.Code.ToString().ToUpperInvariant(),
            };

            await _output.WriteLineAsync($"[{codeLabel}] {check.CheckId}: {check.Message}");
        }

        await _output.WriteLineAsync(
            $"Summary: PASS={_report.PassCount} FAIL={_report.FailCount} WARN={_report.WarnCount} ACTION={_report.ActionCount}");
    }

    private static bool IsSecretLikeFlag(string flag) =>
        flag.Contains("key", StringComparison.OrdinalIgnoreCase)
        || flag.Contains("connection", StringComparison.OrdinalIgnoreCase)
        || flag.Contains("token", StringComparison.OrdinalIgnoreCase)
        || flag.Contains("password", StringComparison.OrdinalIgnoreCase)
        || flag.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || flag.Contains("email", StringComparison.OrdinalIgnoreCase)
        || flag.Contains("recipient", StringComparison.OrdinalIgnoreCase)
        || flag.Contains("sender", StringComparison.OrdinalIgnoreCase);

    private static bool IsSecretLikeValue(string value) =>
        LooksLikeConnectionString(value)
        || value.Contains('@')
        || value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeConnectionString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("AccessKey=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("AccountKey=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("SharedAccessKey=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Endpoint=https://", StringComparison.OrdinalIgnoreCase)
            || value.Contains("DefaultEndpointsProtocol=", StringComparison.OrdinalIgnoreCase);
    }
}
