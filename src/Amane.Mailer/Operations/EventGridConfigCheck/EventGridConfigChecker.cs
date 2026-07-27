using System.Text.Json;
using Amane.Mailer.Bounce;

namespace Amane.Mailer.Operations.EventGridConfigCheck;

/// <summary>
/// Read-only Event Grid and Storage Queue configuration checker for ACS Delivery Reports (#427).
/// </summary>
public sealed class EventGridConfigChecker
{
    public const string CommunicationResourceType = "Microsoft.Communication/CommunicationServices";
    public const string StorageQueueEndpointType = "StorageQueue";
    public const string EventGridSchema = "EventGridSchema";

    private readonly IAzureCliRunner _runner;
    private readonly EventGridConfigCheckOptions _options;
    private readonly SetupDoctorReport _report;

    public EventGridConfigChecker(
        IAzureCliRunner runner,
        EventGridConfigCheckOptions options,
        SetupDoctorReport report)
    {
        _runner = runner;
        _options = options;
        _report = report;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!await EnsureAzureCliAsync(cancellationToken))
        {
            AddManualVerificationActions();
            return;
        }

        if (!await EnsureAccountAsync(cancellationToken))
        {
            AddManualVerificationActions();
            return;
        }

        var acsResourceId = await ResolveAndCheckAcsAsync(cancellationToken);
        if (acsResourceId is null)
        {
            AddManualVerificationActions();
            return;
        }

        await CheckEventSubscriptionAsync(acsResourceId, cancellationToken);
        var storageResourceId = await CheckStorageAccountAsync(cancellationToken);
        await CheckQueueExistsAsync(cancellationToken);
        CheckEnvironmentIsolation(acsResourceId, storageResourceId);
        AddManualVerificationActions();
    }

    private async Task<bool> EnsureAzureCliAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            new AzureCliQuery(AzureCliQueryKind.Version, _options.Subscription),
            cancellationToken);

        if (!result.Started)
        {
            _report.AddFail("az_cli", "Azure CLI is not available on PATH.");
            _report.AddAction("az_cli", "Install Azure CLI and ensure `az` is on PATH, then re-run.");
            return false;
        }

        if (result.TimedOut || result.ExitCode != 0)
        {
            _report.AddFail("az_cli", AzureResourceIdSanitizer.ClassifyCliFailure(result));
            return false;
        }

        _report.AddPass("az_cli", "Azure CLI is available.");
        return true;
    }

    private async Task<bool> EnsureAccountAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            new AzureCliQuery(AzureCliQueryKind.AccountShow, _options.Subscription),
            cancellationToken);

        if (!result.Started || result.TimedOut || result.ExitCode != 0)
        {
            _report.AddFail("az_auth", AzureResourceIdSanitizer.ClassifyCliFailure(result));
            _report.AddAction("az_auth", "Run `az login` with a read-capable identity, then re-run this check.");
            return false;
        }

        if (!TryGetStringProperty(result.StandardOutput, "id", out var subscriptionId)
            && !TryGetStringProperty(result.StandardOutput, "name", out subscriptionId))
        {
            _report.AddFail("az_auth", "Azure CLI account context could not be parsed (details omitted).");
            return false;
        }

        _report.AddPass(
            "az_subscription",
            $"Azure CLI authenticated for requested subscription context ({AzureResourceIdSanitizer.SanitizeSubscription(subscriptionId)}).");
        return true;
    }

    private async Task<string?> ResolveAndCheckAcsAsync(CancellationToken cancellationToken)
    {
        AzureCliQuery query;
        if (!string.IsNullOrWhiteSpace(_options.AcsResourceId))
        {
            query = new AzureCliQuery(
                AzureCliQueryKind.ResourceShow,
                _options.Subscription,
                ResourceId: _options.AcsResourceId);
        }
        else
        {
            query = new AzureCliQuery(
                AzureCliQueryKind.ResourceShow,
                _options.Subscription,
                ResourceGroup: _options.ResourceGroup,
                ResourceName: _options.AcsName,
                ResourceType: CommunicationResourceType);
        }

        var result = await _runner.RunAsync(query, cancellationToken);
        if (!result.Started || result.TimedOut || result.ExitCode != 0)
        {
            _report.AddFail("acs_resource", AzureResourceIdSanitizer.ClassifyCliFailure(result));
            return null;
        }

        if (!TryGetStringProperty(result.StandardOutput, "id", out var resourceId)
            || string.IsNullOrWhiteSpace(resourceId))
        {
            _report.AddFail("acs_resource", "ACS resource response did not include a resource id.");
            return null;
        }

        if (!TryGetStringProperty(result.StandardOutput, "type", out var type)
            || type.IndexOf("CommunicationServices", StringComparison.OrdinalIgnoreCase) < 0)
        {
            _report.AddFail(
                "acs_resource",
                "Resolved resource is not an Azure Communication Services resource.");
            return null;
        }

        _report.AddPass(
            "acs_resource",
            $"ACS resource exists ({AzureResourceIdSanitizer.SanitizeResourceId(resourceId)}).");
        return resourceId;
    }

    private async Task CheckEventSubscriptionAsync(string acsResourceId, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            new AzureCliQuery(
                AzureCliQueryKind.EventSubscriptionShow,
                _options.Subscription,
                EventSubscriptionName: _options.EventSubscriptionName,
                SourceResourceId: acsResourceId),
            cancellationToken);

        if (!result.Started || result.TimedOut || result.ExitCode != 0)
        {
            _report.AddFail("event_subscription", AzureResourceIdSanitizer.ClassifyCliFailure(result));
            return;
        }

        _report.AddPass(
            "event_subscription",
            $"Event Grid subscription exists ({AzureResourceIdSanitizer.SanitizeName(_options.EventSubscriptionName)}).");

        if (!TryParseJson(result.StandardOutput, out var root))
        {
            _report.AddFail("event_subscription_parse", "Event subscription JSON could not be parsed (details omitted).");
            return;
        }

        CheckSource(root, acsResourceId);
        CheckEventTypes(root);
        CheckDestination(root);
        CheckDeliverySchema(root);
    }

    private void CheckSource(JsonElement root, string expectedAcsResourceId)
    {
        var topic = TryGetNestedString(root, "topic")
            ?? TryGetNestedString(root, "source")
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(topic))
        {
            _report.AddFail("event_source", "Event subscription source/topic is missing.");
            return;
        }

        if (!ResourceIdsEqual(topic, expectedAcsResourceId))
        {
            _report.AddFail(
                "event_source",
                $"Event subscription source does not match ACS ({AzureResourceIdSanitizer.SanitizeResourceId(topic)}).");
            return;
        }

        _report.AddPass(
            "event_source",
            $"Event subscription source matches ACS ({AzureResourceIdSanitizer.SanitizeResourceId(topic)}).");
    }

    private void CheckEventTypes(JsonElement root)
    {
        if (!root.TryGetProperty("filter", out var filter)
            || filter.ValueKind != JsonValueKind.Object)
        {
            _report.AddFail(
                "event_types",
                "Event subscription filter is missing; Delivery Report event type cannot be confirmed.");
            return;
        }

        if (!filter.TryGetProperty("includedEventTypes", out var included)
            || included.ValueKind != JsonValueKind.Array)
        {
            _report.AddFail(
                "event_types",
                "includedEventTypes is missing; Delivery Report event type cannot be confirmed.");
            return;
        }

        var types = new List<string>();
        foreach (var item in included.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    types.Add(value);
                }
            }
        }

        if (types.Count == 0)
        {
            _report.AddFail("event_types", "includedEventTypes is empty.");
            return;
        }

        var hasAll = types.Any(static t => string.Equals(t, "All", StringComparison.OrdinalIgnoreCase));
        var hasDeliveryReport = types.Any(static t =>
            string.Equals(t, AcsEventParser.EmailDeliveryReportReceivedEventType, StringComparison.Ordinal));

        if (!hasAll && !hasDeliveryReport)
        {
            _report.AddFail(
                "event_types",
                $"Missing required event type {AcsEventParser.EmailDeliveryReportReceivedEventType}.");
            return;
        }

        _report.AddPass(
            "event_types",
            hasAll
                ? "includedEventTypes includes All (covers EmailDeliveryReportReceived)."
                : "includedEventTypes includes Microsoft.Communication.EmailDeliveryReportReceived.");
    }

    private void CheckDestination(JsonElement root)
    {
        if (!root.TryGetProperty("destination", out var destination)
            || destination.ValueKind != JsonValueKind.Object)
        {
            _report.AddFail("destination", "Event subscription destination is missing.");
            return;
        }

        var endpointType = TryGetNestedString(destination, "endpointType")
            ?? TryGetNestedString(destination, "endpointBase")
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(endpointType)
            && destination.TryGetProperty("properties", out _))
        {
            // Some CLI shapes nest endpointType at the destination root only.
            endpointType = TryGetNestedString(destination, "endpointType") ?? string.Empty;
        }

        if (IsWebhookDestination(endpointType, destination))
        {
            _report.AddFail(
                "destination_type",
                "Destination is a Push webhook. Event Grid Push (#304) is not a valid v1.1.0 bounce transport.");
            _report.AddAction(
                "destination_type",
                "Recreate the subscription with a Storage Queue destination (Pull). Do not treat webhook as success.");
            return;
        }

        if (!string.Equals(endpointType, StorageQueueEndpointType, StringComparison.OrdinalIgnoreCase))
        {
            _report.AddFail(
                "destination_type",
                $"Destination type is not StorageQueue (observed: {AzureResourceIdSanitizer.SanitizeName(string.IsNullOrWhiteSpace(endpointType) ? "(missing)" : endpointType)}).");
            return;
        }

        _report.AddPass("destination_type", "Destination type is StorageQueue.");

        var properties = destination.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
            ? props
            : destination;

        var queueName = TryGetNestedString(properties, "queueName");
        var destinationResourceId = TryGetNestedString(properties, "resourceId");

        if (string.IsNullOrWhiteSpace(queueName)
            || !string.Equals(queueName, _options.QueueName, StringComparison.Ordinal))
        {
            _report.AddFail(
                "destination_queue",
                $"Destination queue name does not match expected value (observed: {AzureResourceIdSanitizer.SanitizeName(queueName)}).");
        }
        else
        {
            _report.AddPass(
                "destination_queue",
                $"Destination queue name matches ({AzureResourceIdSanitizer.SanitizeName(queueName)}).");
        }

        if (string.IsNullOrWhiteSpace(destinationResourceId))
        {
            _report.AddFail("destination_storage", "Destination storage account resource id is missing.");
            return;
        }

        if (!DestinationStorageMatches(destinationResourceId, _options.StorageAccountName))
        {
            _report.AddFail(
                "destination_storage",
                $"Destination storage account does not match expected account ({AzureResourceIdSanitizer.SanitizeResourceId(destinationResourceId)}).");
            return;
        }

        _report.AddPass(
            "destination_storage",
            $"Destination storage account matches ({AzureResourceIdSanitizer.SanitizeResourceId(destinationResourceId)}).");
    }

    private void CheckDeliverySchema(JsonElement root)
    {
        var schema = TryGetNestedString(root, "eventDeliverySchema")
            ?? TryGetNestedString(root, "deliverySchema")
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(schema))
        {
            _report.AddWarn(
                "delivery_schema",
                "eventDeliverySchema was not returned; confirm Event Grid schema matches Mailer parser expectations.");
            return;
        }

        if (schema.Contains("CloudEvent", StringComparison.OrdinalIgnoreCase))
        {
            _report.AddFail(
                "delivery_schema",
                $"Cloud Events delivery schema is incompatible with the current Mailer Event Grid parser (observed: {AzureResourceIdSanitizer.SanitizeName(schema)}).");
            return;
        }

        if (string.Equals(schema, EventGridSchema, StringComparison.OrdinalIgnoreCase))
        {
            _report.AddPass("delivery_schema", "eventDeliverySchema is EventGridSchema.");
            return;
        }

        _report.AddWarn(
            "delivery_schema",
            $"eventDeliverySchema is {AzureResourceIdSanitizer.SanitizeName(schema)}; confirm payloads match AcsEventParser.");
    }

    private async Task<string?> CheckStorageAccountAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            new AzureCliQuery(
                AzureCliQueryKind.StorageAccountShow,
                _options.Subscription,
                ResourceGroup: _options.ResourceGroup,
                StorageAccountName: _options.StorageAccountName),
            cancellationToken);

        if (!result.Started || result.TimedOut || result.ExitCode != 0)
        {
            _report.AddFail("storage_account", AzureResourceIdSanitizer.ClassifyCliFailure(result));
            return null;
        }

        TryGetStringProperty(result.StandardOutput, "id", out var resourceId);
        _report.AddPass(
            "storage_account",
            $"Storage account exists ({AzureResourceIdSanitizer.SanitizeResourceId(resourceId)}).");
        return resourceId;
    }

    private async Task CheckQueueExistsAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            new AzureCliQuery(
                AzureCliQueryKind.StorageQueueExists,
                _options.Subscription,
                StorageAccountName: _options.StorageAccountName,
                QueueName: _options.QueueName),
            cancellationToken);

        if (!result.Started || result.TimedOut || result.ExitCode != 0)
        {
            _report.AddFail("storage_queue", AzureResourceIdSanitizer.ClassifyCliFailure(result));
            return;
        }

        if (!TryParseJson(result.StandardOutput, out var root))
        {
            _report.AddFail("storage_queue", "Queue existence response could not be parsed (details omitted).");
            return;
        }

        var exists = false;
        if (root.ValueKind == JsonValueKind.True)
        {
            exists = true;
        }
        else if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("exists", out var existsProperty))
        {
            exists = existsProperty.ValueKind == JsonValueKind.True
                || (existsProperty.ValueKind == JsonValueKind.String
                    && bool.TryParse(existsProperty.GetString(), out var parsed)
                    && parsed);
        }

        if (!exists)
        {
            _report.AddFail(
                "storage_queue",
                $"Storage queue does not exist ({AzureResourceIdSanitizer.SanitizeName(_options.QueueName)}).");
            return;
        }

        _report.AddPass(
            "storage_queue",
            $"Storage queue exists ({AzureResourceIdSanitizer.SanitizeName(_options.QueueName)}).");
    }

    private void CheckEnvironmentIsolation(string acsResourceId, string? storageResourceId)
    {
        var haystack = string.Join(
            ' ',
            _options.ResourceGroup,
            _options.ResolveAcsDisplayName(),
            _options.StorageAccountName,
            _options.QueueName,
            _options.EventSubscriptionName,
            acsResourceId,
            storageResourceId ?? string.Empty).ToLowerInvariant();

        var environment = EventGridConfigEnvironmentParser.ToDisplay(_options.Environment);
        var conflictTokens = _options.Environment switch
        {
            EventGridConfigEnvironment.Dev => new[] { "staging", "stage", "prod", "production" },
            EventGridConfigEnvironment.Staging => new[] { "prod", "production" },
            EventGridConfigEnvironment.Production => new[] { "staging", "stage", "dev", "devel" },
            _ => Array.Empty<string>(),
        };

        var conflicts = conflictTokens
            .Where(token => ContainsToken(haystack, token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (conflicts.Length > 0)
        {
            _report.AddFail(
                "environment_mix",
                $"Resource names/ids look mixed with other environments ({string.Join(", ", conflicts)}) while --environment={environment}.");
            _report.AddAction(
                "environment_mix",
                "Use environment-isolated ACS / Event Grid / Queue resources (ADR 0020).");
            return;
        }

        var selfTokens = _options.Environment switch
        {
            EventGridConfigEnvironment.Dev => new[] { "dev", "development", "local" },
            EventGridConfigEnvironment.Staging => new[] { "staging", "stage" },
            EventGridConfigEnvironment.Production => new[] { "prod", "production" },
            _ => Array.Empty<string>(),
        };

        if (selfTokens.Any(token => ContainsToken(haystack, token)))
        {
            _report.AddPass(
                "environment_naming",
                $"Resource naming is consistent with --environment={environment} (heuristic).");
        }
        else
        {
            _report.AddWarn(
                "environment_isolation",
                $"Could not confirm environment isolation from resource names for --environment={environment}; verify ACS/Queue are not shared across environments.");
        }
    }

    private void AddManualVerificationActions()
    {
        _report.AddWarn(
            "rbac",
            "Event Grid → Storage Queue RBAC / managed identity permissions are not fully machine-verified.");
        _report.AddAction(
            "rbac",
            "In Azure Portal/CLI, confirm Event Grid can send messages to the target queue (Storage Queue Data Message Sender or equivalent).");

        _report.AddWarn(
            "network",
            "Storage firewall / private endpoint / network rules are not fully machine-verified.");
        _report.AddAction(
            "network",
            "Confirm Event Grid and Mailer can reach the Storage account on the intended network path.");

        _report.AddWarn(
            "arrival",
            "This check does not prove Delivery Report events arrive in the queue.");
        _report.AddAction(
            "arrival",
            "Use maintainer Staging E2E (#428) or bounce runbook procedures for arrival verification. Do not read queue message bodies in this command.");
    }

    private static bool IsWebhookDestination(string endpointType, JsonElement destination)
    {
        if (endpointType.Contains("webhook", StringComparison.OrdinalIgnoreCase)
            || endpointType.Contains("web hook", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var endpointUrl = TryGetNestedString(destination, "endpointUrl")
            ?? (destination.TryGetProperty("properties", out var props)
                ? TryGetNestedString(props, "endpointUrl")
                : null);

        return !string.IsNullOrWhiteSpace(endpointUrl);
    }

    private static bool DestinationStorageMatches(string destinationResourceId, string storageAccountName)
    {
        var sanitizedExpected = storageAccountName.Trim();
        if (destinationResourceId.EndsWith("/" + sanitizedExpected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return destinationResourceId.Contains(
            "/storageAccounts/" + sanitizedExpected,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool ResourceIdsEqual(string left, string right) =>
        string.Equals(left.Trim().TrimEnd('/'), right.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static bool ContainsToken(string haystack, string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var index = 0;
        while ((index = haystack.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetterOrDigit(haystack[index - 1]);
            var afterIndex = index + token.Length;
            var afterOk = afterIndex >= haystack.Length || !char.IsLetterOrDigit(haystack[afterIndex]);
            if (beforeOk && afterOk)
            {
                return true;
            }

            index += token.Length;
        }

        return false;
    }

    private static bool TryParseJson(string json, out JsonElement root)
    {
        root = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetStringProperty(string json, string propertyName, out string value)
    {
        value = string.Empty;
        if (!TryParseJson(json, out var root))
        {
            return false;
        }

        var found = TryGetNestedString(root, propertyName);
        if (string.IsNullOrWhiteSpace(found))
        {
            return false;
        }

        value = found;
        return true;
    }

    private static string? TryGetNestedString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (element.TryGetProperty(propertyName, out var direct)
            && direct.ValueKind == JsonValueKind.String)
        {
            return direct.GetString();
        }

        // Azure CLI may return PascalCase depending on version / resource provider.
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }
}
