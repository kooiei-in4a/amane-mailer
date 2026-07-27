using Amane.Mailer.Operations;
using Amane.Mailer.Operations.EventGridConfigCheck;

namespace Amane.Mailer.Tests;

public sealed class EventGridConfigCheckCliTests
{
    private static readonly string AcsId =
        "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-mailer-staging/providers/Microsoft.Communication/CommunicationServices/acs-mailer-staging";

    private static readonly string StorageId =
        "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-mailer-staging/providers/Microsoft.Storage/storageAccounts/stmailerstaging";

    [Fact]
    public async Task happy_path_storage_queue_subscription_passes_core_checks()
    {
        var runner = FakeAzureCliRunner.HappyPath();
        var (exitCode, output, error) = await RunAsync(runner, StagingArgs());

        Assert.Equal(EventGridConfigCheckCommand.SuccessExitCode, exitCode);
        Assert.Empty(error);
        Assert.Contains("[PASS] az_cli:", output, StringComparison.Ordinal);
        Assert.Contains("[PASS] acs_resource:", output, StringComparison.Ordinal);
        Assert.Contains("[PASS] event_subscription:", output, StringComparison.Ordinal);
        Assert.Contains("[PASS] event_source:", output, StringComparison.Ordinal);
        Assert.Contains("[PASS] event_types:", output, StringComparison.Ordinal);
        Assert.Contains("[PASS] destination_type:", output, StringComparison.Ordinal);
        Assert.Contains("[PASS] destination_queue:", output, StringComparison.Ordinal);
        Assert.Contains("[PASS] storage_queue:", output, StringComparison.Ordinal);
        Assert.Contains("[WARN] rbac:", output, StringComparison.Ordinal);
        Assert.Contains("[ACTION] rbac:", output, StringComparison.Ordinal);
        Assert.Contains("[WARN] network:", output, StringComparison.Ordinal);
        Assert.Contains("[WARN] arrival:", output, StringComparison.Ordinal);
        Assert.Contains("Summary: PASS=", output, StringComparison.Ordinal);
        Assert.DoesNotContain(AcsId, output, StringComparison.Ordinal);
        Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", output, StringComparison.Ordinal);
        Assert.DoesNotContain("AccessKey=", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer ", output, StringComparison.Ordinal);
        AssertDoesNotContainRawJsonDump(output);
    }

    [Fact]
    public async Task missing_event_subscription_fails()
    {
        var runner = FakeAzureCliRunner.HappyPath();
        runner.Set(AzureCliQueryKind.EventSubscriptionShow, new AzureCliRunResult(
            true, 3, string.Empty, "ResourceNotFound: could not be found", false));

        var (exitCode, output, _) = await RunAsync(runner, StagingArgs());

        Assert.Equal(EventGridConfigCheckCommand.FailureExitCode, exitCode);
        Assert.Contains("[FAIL] event_subscription:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ResourceNotFound: could not be found", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task source_acs_mismatch_fails()
    {
        var runner = FakeAzureCliRunner.HappyPath();
        runner.Set(AzureCliQueryKind.EventSubscriptionShow, Ok(SubscriptionJson(
            topic: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-mailer-staging/providers/Microsoft.Communication/CommunicationServices/other-acs",
            endpointType: "StorageQueue",
            queueName: "bounce-staging",
            storageId: StorageId)));

        var (exitCode, output, _) = await RunAsync(runner, StagingArgs());

        Assert.Equal(EventGridConfigCheckCommand.FailureExitCode, exitCode);
        Assert.Contains("[FAIL] event_source:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task destination_queue_mismatch_fails()
    {
        var runner = FakeAzureCliRunner.HappyPath();
        runner.Set(AzureCliQueryKind.EventSubscriptionShow, Ok(SubscriptionJson(
            topic: AcsId,
            endpointType: "StorageQueue",
            queueName: "wrong-queue",
            storageId: StorageId)));

        var (exitCode, output, _) = await RunAsync(runner, StagingArgs());

        Assert.Equal(EventGridConfigCheckCommand.FailureExitCode, exitCode);
        Assert.Contains("[FAIL] destination_queue:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task missing_delivery_report_event_type_fails()
    {
        var runner = FakeAzureCliRunner.HappyPath();
        runner.Set(AzureCliQueryKind.EventSubscriptionShow, Ok(SubscriptionJson(
            topic: AcsId,
            endpointType: "StorageQueue",
            queueName: "bounce-staging",
            storageId: StorageId,
            eventTypes: ["Microsoft.Communication.EmailStatusUpdated"])));

        var (exitCode, output, _) = await RunAsync(runner, StagingArgs());

        Assert.Equal(EventGridConfigCheckCommand.FailureExitCode, exitCode);
        Assert.Contains("[FAIL] event_types:", output, StringComparison.Ordinal);
        Assert.Contains("EmailDeliveryReportReceived", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task included_event_types_all_without_delivery_report_fails()
    {
        var runner = FakeAzureCliRunner.HappyPath();
        runner.Set(AzureCliQueryKind.EventSubscriptionShow, Ok(SubscriptionJson(
            topic: AcsId,
            endpointType: "StorageQueue",
            queueName: "bounce-staging",
            storageId: StorageId,
            eventTypes: ["All"])));

        var (exitCode, output, _) = await RunAsync(runner, StagingArgs());

        Assert.Equal(EventGridConfigCheckCommand.FailureExitCode, exitCode);
        Assert.Contains("[FAIL] event_types:", output, StringComparison.Ordinal);
        Assert.Contains("All is not accepted", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task destination_storage_subscription_mismatch_fails()
    {
        var otherStorageId =
            "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-mailer-staging/providers/Microsoft.Storage/storageAccounts/stmailerstaging";
        var runner = FakeAzureCliRunner.HappyPath();
        runner.Set(AzureCliQueryKind.EventSubscriptionShow, Ok(SubscriptionJson(
            topic: AcsId,
            endpointType: "StorageQueue",
            queueName: "bounce-staging",
            storageId: otherStorageId)));

        var (exitCode, output, _) = await RunAsync(runner, StagingArgs());

        Assert.Equal(EventGridConfigCheckCommand.FailureExitCode, exitCode);
        Assert.Contains("[FAIL] destination_storage:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("22222222-2222-2222-2222-222222222222", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task destination_storage_resource_group_mismatch_fails()
    {
        var otherStorageId =
            "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-other/providers/Microsoft.Storage/storageAccounts/stmailerstaging";
        var runner = FakeAzureCliRunner.HappyPath();
        runner.Set(AzureCliQueryKind.EventSubscriptionShow, Ok(SubscriptionJson(
            topic: AcsId,
            endpointType: "StorageQueue",
            queueName: "bounce-staging",
            storageId: otherStorageId)));

        var (exitCode, output, _) = await RunAsync(runner, StagingArgs());

        Assert.Equal(EventGridConfigCheckCommand.FailureExitCode, exitCode);
        Assert.Contains("[FAIL] destination_storage:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task storage_account_response_missing_id_fails()
    {
        var runner = FakeAzureCliRunner.HappyPath();
        runner.Set(AzureCliQueryKind.StorageAccountShow, Ok("""{"name":"stmailerstaging"}"""));

        var (exitCode, output, _) = await RunAsync(runner, StagingArgs());

        Assert.Equal(EventGridConfigCheckCommand.FailureExitCode, exitCode);
        Assert.Contains("[FAIL] storage_account:", output, StringComparison.Ordinal);
        Assert.Contains("did not include a resource id", output, StringComparison.Ordinal);
        Assert.Contains("[FAIL] destination_storage:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task push_webhook_destination_is_not_treated_as_valid()
    {
        var runner = FakeAzureCliRunner.HappyPath();
        runner.Set(AzureCliQueryKind.EventSubscriptionShow, Ok("""
            {
              "topic": "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-mailer-staging/providers/Microsoft.Communication/CommunicationServices/acs-mailer-staging",
              "filter": { "includedEventTypes": ["Microsoft.Communication.EmailDeliveryReportReceived"] },
              "destination": {
                "endpointType": "WebHook",
                "properties": { "endpointUrl": "https://example.invalid/hooks/events" }
              },
              "eventDeliverySchema": "EventGridSchema"
            }
            """));

        var (exitCode, output, _) = await RunAsync(runner, StagingArgs());

        Assert.Equal(EventGridConfigCheckCommand.FailureExitCode, exitCode);
        Assert.Contains("[FAIL] destination_type:", output, StringComparison.Ordinal);
        Assert.Contains("Push webhook", output, StringComparison.Ordinal);
        Assert.Contains("#304", output, StringComparison.Ordinal);
        Assert.DoesNotContain("https://example.invalid", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task queue_missing_fails()
    {
        var runner = FakeAzureCliRunner.HappyPath();
        runner.Set(AzureCliQueryKind.StorageQueueExists, Ok("""{"exists": false}"""));

        var (exitCode, output, _) = await RunAsync(runner, StagingArgs());

        Assert.Equal(EventGridConfigCheckCommand.FailureExitCode, exitCode);
        Assert.Contains("[FAIL] storage_queue:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task subscription_context_auth_failure_is_sanitized()
    {
        var runner = new FakeAzureCliRunner();
        runner.Set(AzureCliQueryKind.Version, Ok("""{"azure-cli":"2.60.0"}"""));
        runner.Set(AzureCliQueryKind.AccountShow, new AzureCliRunResult(
            true,
            1,
            string.Empty,
            "Please run 'az login' to setup account. token=eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.aaa.bbb",
            false));

        var (exitCode, output, _) = await RunAsync(runner, StagingArgs());

        Assert.Equal(EventGridConfigCheckCommand.FailureExitCode, exitCode);
        Assert.Contains("[FAIL] az_auth:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", output, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task az_cli_missing_fails()
    {
        var runner = new FakeAzureCliRunner();
        runner.Set(AzureCliQueryKind.Version, new AzureCliRunResult(false, -1, string.Empty, string.Empty, false));

        var (exitCode, output, _) = await RunAsync(runner, StagingArgs());

        Assert.Equal(EventGridConfigCheckCommand.FailureExitCode, exitCode);
        Assert.Contains("[FAIL] az_cli:", output, StringComparison.Ordinal);
        Assert.Contains("[ACTION] az_cli:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task environment_mix_is_detected()
    {
        var runner = FakeAzureCliRunner.HappyPath(
            acsName: "acs-mailer-prod",
            storageName: "stmailerstaging",
            queueName: "bounce-staging",
            resourceGroup: "rg-mailer-staging");

        var args = BaseArgs(
            "--acs-name", "acs-mailer-prod",
            "--storage-account", "stmailerstaging",
            "--queue-name", "bounce-staging",
            "--resource-group", "rg-mailer-staging",
            "--environment", "staging");

        var (exitCode, output, _) = await RunAsync(runner, args);

        Assert.Equal(EventGridConfigCheckCommand.FailureExitCode, exitCode);
        Assert.Contains("[FAIL] environment_mix:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task cloud_event_schema_fails()
    {
        var runner = FakeAzureCliRunner.HappyPath();
        runner.Set(AzureCliQueryKind.EventSubscriptionShow, Ok(SubscriptionJson(
            topic: AcsId,
            endpointType: "StorageQueue",
            queueName: "bounce-staging",
            storageId: StorageId,
            schema: "CloudEventSchemaV1_0")));

        var (exitCode, output, _) = await RunAsync(runner, StagingArgs());

        Assert.Equal(EventGridConfigCheckCommand.FailureExitCode, exitCode);
        Assert.Contains("[FAIL] delivery_schema:", output, StringComparison.Ordinal);
    }

    public static TheoryData<AzureCliQueryKind, string> AllowlistedQueryPrefixes() => new()
    {
        { AzureCliQueryKind.Version, "version --output json" },
        { AzureCliQueryKind.AccountShow, "account show --subscription " },
        { AzureCliQueryKind.ResourceShow, "resource show --resource-group " },
        { AzureCliQueryKind.EventSubscriptionShow, "eventgrid event-subscription show --name " },
        { AzureCliQueryKind.StorageAccountShow, "storage account show --name " },
        { AzureCliQueryKind.StorageQueueExists, "storage queue exists --name " },
    };

    [Theory]
    [MemberData(nameof(AllowlistedQueryPrefixes))]
    public void argument_builder_emits_only_allowlisted_read_query_prefixes(
        AzureCliQueryKind kind,
        string expectedPrefix)
    {
        var query = kind switch
        {
            AzureCliQueryKind.Version => new AzureCliQuery(kind, "sub-1"),
            AzureCliQueryKind.AccountShow => new AzureCliQuery(kind, "sub-1"),
            AzureCliQueryKind.ResourceShow => new AzureCliQuery(
                kind,
                "sub-1",
                ResourceGroup: "rg",
                ResourceName: "acs",
                ResourceType: EventGridConfigChecker.CommunicationResourceType),
            AzureCliQueryKind.EventSubscriptionShow => new AzureCliQuery(
                kind,
                "sub-1",
                EventSubscriptionName: "eg-sub",
                SourceResourceId: AcsId),
            AzureCliQueryKind.StorageAccountShow => new AzureCliQuery(
                kind,
                "sub-1",
                ResourceGroup: "rg",
                StorageAccountName: "st"),
            AzureCliQueryKind.StorageQueueExists => new AzureCliQuery(
                kind,
                "sub-1",
                StorageAccountName: "st",
                QueueName: "q"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var args = AzureCliArgumentBuilder.Build(query);

        Assert.StartsWith(expectedPrefix, args, StringComparison.Ordinal);
        Assert.DoesNotContain("account set", args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("create", args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("update", args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delete", args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role assignment", args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("network", args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("firewall", args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task azure_cli_runner_starts_path_script_without_shell_execute()
    {
        var tempDir = Directory.CreateTempSubdirectory("amane-az-runner-");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var scriptPath = Path.Combine(tempDir.FullName, "az.cmd");
                await File.WriteAllTextAsync(
                    scriptPath,
                    "@echo off\r\necho {\"azure-cli\":\"test-fixture\"}\r\nexit /b 0\r\n",
                    TestContext.Current.CancellationToken);
            }
            else
            {
                var scriptPath = Path.Combine(tempDir.FullName, "az");
                await File.WriteAllTextAsync(
                    scriptPath,
                    "#!/bin/sh\necho '{\"azure-cli\":\"test-fixture\"}'\nexit 0\n",
                    TestContext.Current.CancellationToken);
                File.SetUnixFileMode(
                    scriptPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Environment.SetEnvironmentVariable(
                "PATH",
                tempDir.FullName + Path.PathSeparator + (originalPath ?? string.Empty));

            var runner = new AzureCliRunner();
            var result = await runner.RunAsync(
                new AzureCliQuery(AzureCliQueryKind.Version, "sub-1"),
                TestContext.Current.CancellationToken);

            Assert.True(result.Started);
            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("test-fixture", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            try
            {
                tempDir.Delete(recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Fact]
    public void build_windows_cmd_arguments_preserves_nested_quotes_for_spaced_path_and_subscription()
    {
        var built = AzureCliRunner.BuildWindowsCmdArguments(
            @"C:\Program Files\Azure CLI\az.cmd",
            "account show --subscription \"Mailer Staging\" --output json");

        Assert.Equal(
            "/d /s /v:off /c \"\"C:\\Program Files\\Azure CLI\\az.cmd\" account show --subscription \"Mailer Staging\" --output json\"",
            built);
    }

    [Fact]
    public void build_windows_cmd_arguments_rejects_bang_in_az_path()
    {
        Assert.Throws<ArgumentException>(() =>
            AzureCliRunner.BuildWindowsCmdArguments(@"C:\tools\az!cli\az.cmd", "version --output json"));
    }

    [Fact]
    public void argument_builder_rejects_bang_in_subscription()
    {
        Assert.Throws<ArgumentException>(() =>
            AzureCliArgumentBuilder.Build(new AzureCliQuery(
                AzureCliQueryKind.AccountShow,
                "Mailer Staging!")));
    }

    [Fact]
    public async Task azure_cli_runner_preserves_spaced_path_and_subscription_argv_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "amane az runner " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            var scriptPath = Path.Combine(root, "az.cmd");
            await File.WriteAllTextAsync(
                scriptPath,
                "@echo off\r\necho ARGS=%*\r\nexit /b 0\r\n",
                TestContext.Current.CancellationToken);

            Environment.SetEnvironmentVariable(
                "PATH",
                root + Path.PathSeparator + (originalPath ?? string.Empty));

            var runner = new AzureCliRunner();
            var result = await runner.RunAsync(
                new AzureCliQuery(AzureCliQueryKind.AccountShow, "Mailer Staging"),
                TestContext.Current.CancellationToken);

            Assert.True(result.Started, result.StandardError);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("account", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("show", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Mailer Staging", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("--output", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("json", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Fact]
    public void parse_rejects_connection_string_like_input()
    {
        var args = new[]
        {
            "setup", "check-event-grid",
            "--subscription", "sub",
            "--resource-group", "rg",
            "--acs-name", "Endpoint=https://example.communication.azure.com/;AccessKey=not-a-real-key",
            "--event-subscription", "eg",
            "--storage-account", "st",
            "--queue-name", "q",
            "--environment", "staging",
        };

        var ok = EventGridConfigCheckCommand.TryParseArguments(args, out _, out var usageError);

        Assert.False(ok);
        Assert.Contains("must not be supplied", usageError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void parse_requires_exactly_one_acs_locator()
    {
        var args = new[]
        {
            "setup", "check-event-grid",
            "--subscription", "sub",
            "--resource-group", "rg",
            "--event-subscription", "eg",
            "--storage-account", "st",
            "--queue-name", "q",
            "--environment", "staging",
        };

        var ok = EventGridConfigCheckCommand.TryParseArguments(args, out _, out var usageError);

        Assert.False(ok);
        Assert.Contains("--acs-name", usageError, StringComparison.Ordinal);
    }

    [Fact]
    public void sanitizer_masks_subscription_guid_and_secrets()
    {
        var sanitized = AzureResourceIdSanitizer.SanitizeResourceId(AcsId);
        Assert.Contains("/subscriptions/***/", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", sanitized, StringComparison.Ordinal);

        var redacted = AzureResourceIdSanitizer.RedactSecrets(
            "Bearer abc.def.ghi AccessKey=super-secret user@example.com");
        Assert.DoesNotContain("abc.def.ghi", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", redacted, StringComparison.Ordinal);
    }

    private static string[] StagingArgs() =>
        BaseArgs(
            "--acs-name", "acs-mailer-staging",
            "--storage-account", "stmailerstaging",
            "--queue-name", "bounce-staging",
            "--resource-group", "rg-mailer-staging",
            "--environment", "staging");

    private static string[] BaseArgs(params string[] extras)
    {
        var list = new List<string>
        {
            "setup", "check-event-grid",
            "--subscription", "11111111-1111-1111-1111-111111111111",
            "--event-subscription", "acs-delivery-reports",
        };
        list.AddRange(extras);
        return list.ToArray();
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        IAzureCliRunner runner,
        string[] args)
    {
        Assert.True(EventGridConfigCheckCommand.TryParseArguments(args, out var options, out var usageError), usageError);
        Assert.NotNull(options);

        await using var output = new StringWriter();
        await using var error = new StringWriter();
        var command = new EventGridConfigCheckCommand(runner, options, output, error);
        var exitCode = await command.ExecuteAsync(CancellationToken.None);
        return (exitCode, output.ToString(), error.ToString());
    }

    private static AzureCliRunResult Ok(string json) =>
        new(true, 0, json, string.Empty, false);

    private static string SubscriptionJson(
        string topic,
        string endpointType,
        string queueName,
        string storageId,
        string[]? eventTypes = null,
        string schema = "EventGridSchema")
    {
        eventTypes ??= ["Microsoft.Communication.EmailDeliveryReportReceived"];
        var typesJson = string.Join(",", eventTypes.Select(static t => $"\"{t}\""));
        return $$"""
            {
              "topic": "{{topic}}",
              "filter": { "includedEventTypes": [{{typesJson}}] },
              "destination": {
                "endpointType": "{{endpointType}}",
                "properties": {
                  "resourceId": "{{storageId}}",
                  "queueName": "{{queueName}}"
                }
              },
              "eventDeliverySchema": "{{schema}}"
            }
            """;
    }

    private static void AssertDoesNotContainRawJsonDump(string output)
    {
        Assert.DoesNotContain("\"destination\":", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"includedEventTypes\":", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"endpointType\":", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"resourceId\":", output, StringComparison.Ordinal);
    }

    private sealed class FakeAzureCliRunner : IAzureCliRunner
    {
        private readonly Dictionary<AzureCliQueryKind, AzureCliRunResult> _results = new();

        public void Set(AzureCliQueryKind kind, AzureCliRunResult result) =>
            _results[kind] = result;

        public Task<AzureCliRunResult> RunAsync(AzureCliQuery query, CancellationToken cancellationToken)
        {
            if (!_results.TryGetValue(query.Kind, out var result))
            {
                throw new InvalidOperationException($"No fake result for {query.Kind}");
            }

            // Ensure argument builder stays allowlisted for every exercised query.
            _ = AzureCliArgumentBuilder.Build(query);
            return Task.FromResult(result);
        }

        public static FakeAzureCliRunner HappyPath(
            string acsName = "acs-mailer-staging",
            string storageName = "stmailerstaging",
            string queueName = "bounce-staging",
            string resourceGroup = "rg-mailer-staging")
        {
            var acsId =
                $"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/{resourceGroup}/providers/Microsoft.Communication/CommunicationServices/{acsName}";
            var storageId =
                $"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/{resourceGroup}/providers/Microsoft.Storage/storageAccounts/{storageName}";

            var runner = new FakeAzureCliRunner();
            runner.Set(AzureCliQueryKind.Version, Ok("""{"azure-cli":"2.60.0"}"""));
            runner.Set(AzureCliQueryKind.AccountShow, Ok("""{"id":"11111111-1111-1111-1111-111111111111","name":"staging"}"""));
            runner.Set(AzureCliQueryKind.ResourceShow, Ok($$"""{"id":"{{acsId}}","type":"Microsoft.Communication/CommunicationServices","name":"{{acsName}}"}"""));
            runner.Set(AzureCliQueryKind.EventSubscriptionShow, Ok(SubscriptionJson(
                topic: acsId,
                endpointType: "StorageQueue",
                queueName: queueName,
                storageId: storageId)));
            runner.Set(AzureCliQueryKind.StorageAccountShow, Ok($$"""{"id":"{{storageId}}","name":"{{storageName}}"}"""));
            runner.Set(AzureCliQueryKind.StorageQueueExists, Ok("""{"exists": true}"""));
            return runner;
        }
    }
}
