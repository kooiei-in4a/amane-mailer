using System.Text.Json;
using Amane.Mailer.Json;
using Amane.Mailer.Operations.AcsSetup;
using Amane.Mailer.Setup.Assistant;

namespace Amane.Mailer.Setup.NonInteractive;

internal sealed class SetupNonInteractiveValidationFailure
{
    internal required string Code { get; init; }
    internal string? Mode { get; init; }
    internal string? ActionCode { get; init; }
}

/// <summary>
/// schemaVersion 1 validation for non-interactive setup config (issue #453).
/// </summary>
internal static class SetupNonInteractiveInputValidator
{
    private const int MaxNestDepth = 8;

    private static readonly HashSet<string> RootFields = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "mode",
        "tenant",
        "serviceToken",
        "acsConnectionString",
        "environmentConfirmation",
        "intentConfirmation",
        "stagingRecipientEmail",
        "stagingIntentConfirmation",
        "liveSendingEnableApproval",
    };

    private static readonly HashSet<string> TenantFields = new(StringComparer.Ordinal)
    {
        "tenantId",
        "tenantName",
        "sourceService",
        "senderEmail",
        "senderDisplayName",
    };

    internal static bool TryParse(
        string json,
        out SetupNonInteractiveInput? input,
        out SetupNonInteractiveValidationFailure? failure)
    {
        input = null;
        failure = null;

        try
        {
            if (JsonDuplicatePropertyDetector.HasDuplicateProperty(json))
            {
                failure = Fail(SetupNonInteractiveResultCode.DuplicateProperty);
                return false;
            }
        }
        catch (JsonException)
        {
            failure = Fail(SetupNonInteractiveResultCode.InvalidJson);
            return false;
        }

        if (HasNestedTooDeep(json))
        {
            failure = Fail(SetupNonInteractiveResultCode.NestedTooDeep);
            return false;
        }

        if (HasAdminPropertyName(json))
        {
            failure = Fail(
                SetupNonInteractiveResultCode.AdminInputRejected,
                actionCode: SetupNonInteractiveResultCode.UseSetupAssistantAction);
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            failure = Fail(SetupNonInteractiveResultCode.InvalidJson);
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                failure = Fail(SetupNonInteractiveResultCode.InvalidJson);
                return false;
            }

            if (HasUnknownRootField(document.RootElement))
            {
                failure = Fail(SetupNonInteractiveResultCode.UnknownField);
                return false;
            }

            if (!TryReadInt(document.RootElement, "schemaVersion", out var schemaVersion)
                || schemaVersion != 1)
            {
                failure = Fail(SetupNonInteractiveResultCode.UnsupportedSchema);
                return false;
            }

            if (!TryReadString(document.RootElement, "mode", out var modeRaw))
            {
                failure = Fail(SetupNonInteractiveResultCode.MissingRequiredField);
                return false;
            }

            if (string.Equals(modeRaw, "production-queue", StringComparison.Ordinal))
            {
                failure = Fail(SetupNonInteractiveResultCode.ModeNotSupported, mode: modeRaw);
                return false;
            }

            if (!SetupAssistantInputs.TryParseAutomatableMode(modeRaw, out var mode))
            {
                failure = Fail(SetupNonInteractiveResultCode.InvalidMode, mode: modeRaw);
                return false;
            }

            var wireMode = SetupModeParser.ToWireValue(mode);

            if (!document.RootElement.TryGetProperty("tenant", out var tenantElement)
                || tenantElement.ValueKind != JsonValueKind.Object)
            {
                failure = Fail(SetupNonInteractiveResultCode.InvalidTenant, mode: wireMode);
                return false;
            }

            if (HasUnknownTenantField(tenantElement))
            {
                failure = Fail(SetupNonInteractiveResultCode.UnknownField, mode: wireMode);
                return false;
            }

            if (!TryReadString(tenantElement, "tenantId", out var tenantIdRaw))
            {
                failure = Fail(SetupNonInteractiveResultCode.MissingRequiredField, mode: wireMode);
                return false;
            }

            if (!Guid.TryParse(tenantIdRaw, out var tenantId))
            {
                failure = Fail(SetupNonInteractiveResultCode.InvalidTenantId, mode: wireMode);
                return false;
            }

            if (!TryReadString(tenantElement, "tenantName", out var tenantName)
                || !SetupAssistantInputs.IsIdentifier(tenantName))
            {
                failure = Fail(SetupNonInteractiveResultCode.InvalidIdentifier, mode: wireMode);
                return false;
            }

            if (!TryReadString(tenantElement, "sourceService", out var sourceService)
                || !SetupAssistantInputs.IsSourceService(sourceService))
            {
                failure = Fail(SetupNonInteractiveResultCode.InvalidSourceService, mode: wireMode);
                return false;
            }

            if (!TryReadString(tenantElement, "senderEmail", out var senderEmail)
                || !SetupAssistantInputs.IsEmail(senderEmail))
            {
                failure = Fail(SetupNonInteractiveResultCode.InvalidEmail, mode: wireMode);
                return false;
            }

            if (!TryReadString(tenantElement, "senderDisplayName", out var senderDisplayName)
                || !SetupAssistantInputs.IsDisplayText(senderDisplayName))
            {
                failure = Fail(SetupNonInteractiveResultCode.InvalidDisplayName, mode: wireMode);
                return false;
            }

            if (!TryReadString(document.RootElement, "serviceToken", out var serviceToken)
                || !SetupAssistantInputs.IsSecret(serviceToken))
            {
                failure = Fail(SetupNonInteractiveResultCode.InvalidSecret, mode: wireMode);
                return false;
            }

            string? acsConnectionString = null;
            string? environmentConfirmation = null;
            string? intentConfirmation = null;
            string? stagingRecipientEmail = null;
            string? stagingIntentConfirmation = null;
            string? liveSendingEnableApproval = null;

            if (mode == SetupMode.LocalMailpit)
            {
                if (HasForbiddenMailpitField(document.RootElement))
                {
                    failure = Fail(SetupNonInteractiveResultCode.ForbiddenField, mode: wireMode);
                    return false;
                }
            }
            else
            {
                if (!TryReadString(document.RootElement, "acsConnectionString", out acsConnectionString)
                    || !SetupAssistantInputs.IsSecret(acsConnectionString))
                {
                    failure = Fail(SetupNonInteractiveResultCode.InvalidAcs, mode: wireMode);
                    return false;
                }

                if (!TryReadString(document.RootElement, "environmentConfirmation", out environmentConfirmation)
                    || !AcsEnvironmentConfirmation.TryMap(environmentConfirmation, out _))
                {
                    failure = Fail(SetupNonInteractiveResultCode.ConfirmationPhraseMismatch, mode: wireMode);
                    return false;
                }

                if (!TryReadString(document.RootElement, "intentConfirmation", out intentConfirmation)
                    || !string.Equals(intentConfirmation, AcsRegisterOperation.IntentPhrase, StringComparison.Ordinal))
                {
                    failure = Fail(SetupNonInteractiveResultCode.ConfirmationPhraseMismatch, mode: wireMode);
                    return false;
                }

                if (mode is SetupMode.StagingNoSend or SetupMode.ProductionAcs)
                {
                    if (document.RootElement.TryGetProperty("stagingRecipientEmail", out _)
                        || document.RootElement.TryGetProperty("stagingIntentConfirmation", out _))
                    {
                        failure = Fail(SetupNonInteractiveResultCode.ForbiddenField, mode: wireMode);
                        return false;
                    }
                }

                if (mode == SetupMode.StagingVerification)
                {
                    if (!TryReadString(document.RootElement, "stagingRecipientEmail", out stagingRecipientEmail)
                        || !SetupAssistantInputs.IsEmail(stagingRecipientEmail))
                    {
                        failure = Fail(SetupNonInteractiveResultCode.InvalidEmail, mode: wireMode);
                        return false;
                    }

                    if (!TryReadString(document.RootElement, "stagingIntentConfirmation", out stagingIntentConfirmation)
                        || !string.Equals(
                            stagingIntentConfirmation,
                            AcsStagingVerificationOperation.IntentPhrase,
                            StringComparison.Ordinal))
                    {
                        failure = Fail(SetupNonInteractiveResultCode.ConfirmationPhraseMismatch, mode: wireMode);
                        return false;
                    }
                }

                if (mode == SetupMode.ProductionAcs)
                {
                    if (!TryReadString(document.RootElement, "liveSendingEnableApproval", out liveSendingEnableApproval)
                        || !string.Equals(
                            liveSendingEnableApproval,
                            AcsLiveSendingApproval.EnablePhrase,
                            StringComparison.Ordinal))
                    {
                        failure = Fail(SetupNonInteractiveResultCode.ConfirmationPhraseMismatch, mode: wireMode);
                        return false;
                    }
                }
                else if (document.RootElement.TryGetProperty("liveSendingEnableApproval", out _))
                {
                    failure = Fail(SetupNonInteractiveResultCode.ForbiddenField, mode: wireMode);
                    return false;
                }

                if (mode != SetupMode.StagingVerification
                    && (document.RootElement.TryGetProperty("stagingRecipientEmail", out _)
                        || document.RootElement.TryGetProperty("stagingIntentConfirmation", out _)))
                {
                    failure = Fail(SetupNonInteractiveResultCode.ForbiddenField, mode: wireMode);
                    return false;
                }

                var expectedEnvironment = SetupAssistantInputs.EnvironmentConfirmationFor(mode);
                if (!string.Equals(environmentConfirmation, expectedEnvironment, StringComparison.Ordinal))
                {
                    failure = Fail(SetupNonInteractiveResultCode.ConfirmationPhraseMismatch, mode: wireMode);
                    return false;
                }
            }

            input = new SetupNonInteractiveInput
            {
                Mode = mode,
                TenantId = tenantId,
                TenantName = tenantName,
                SourceService = sourceService,
                SenderEmail = senderEmail,
                SenderDisplayName = senderDisplayName,
                ServiceToken = serviceToken,
                AcsConnectionString = acsConnectionString,
                EnvironmentConfirmation = environmentConfirmation,
                IntentConfirmation = intentConfirmation,
                StagingRecipientEmail = stagingRecipientEmail,
                StagingIntentConfirmation = stagingIntentConfirmation,
                LiveSendingEnableApproval = liveSendingEnableApproval,
            };
            return true;
        }
    }

    private static SetupNonInteractiveValidationFailure Fail(
        string code,
        string? mode = null,
        string? actionCode = null) =>
        new() { Code = code, Mode = mode, ActionCode = actionCode };

    private static bool TryReadString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrEmpty(value);
    }

    private static bool TryReadInt(JsonElement element, string name, out int value)
    {
        value = default;
        if (!element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out value))
        {
            return false;
        }

        return true;
    }

    private static bool HasUnknownRootField(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!RootFields.Contains(property.Name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUnknownTenantField(JsonElement tenant)
    {
        foreach (var property in tenant.EnumerateObject())
        {
            if (!TenantFields.Contains(property.Name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasForbiddenMailpitField(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "acsConnectionString":
                case "environmentConfirmation":
                case "intentConfirmation":
                case "stagingRecipientEmail":
                case "stagingIntentConfirmation":
                case "liveSendingEnableApproval":
                    return true;
            }
        }

        return false;
    }

    private static bool HasNestedTooDeep(string json)
    {
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
        var depth = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                depth++;
                if (depth > MaxNestDepth)
                {
                    return true;
                }
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                depth--;
            }
        }

        return false;
    }

    private static bool HasAdminPropertyName(string json)
    {
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var name = reader.GetString() ?? string.Empty;
            if (IsAdminPropertyName(name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAdminPropertyName(string name) =>
        name is "admin"
            or "adminEnabled"
            or "adminUsername"
            or "adminPassword"
            or "adminPasswordHash"
            or "adminPasswordHashFile"
            or "passwordHash"
            or "passwordHashFile"
            || name.StartsWith("AMANE_ADMIN_", StringComparison.Ordinal);
}
