using Amane.Mailer.Configuration;
using Amane.Mailer.Operations.AcsSetup;
using Amane.Mailer.Setup.Assistant;

namespace Amane.Mailer.Setup.NonInteractive;

/// <summary>
/// Loads and validates schema v1 non-interactive config from a TOCTOU-safe file read.
/// </summary>
internal static class SetupNonInteractiveInputParser
{
    internal sealed class ParseOutcome
    {
        internal required bool Succeeded { get; init; }
        internal SetupNonInteractiveInput? Input { get; init; }
        internal SetupNonInteractiveValidationFailure? Failure { get; init; }
        internal string FailureCode { get; init; } = string.Empty;
    }

    internal static ParseOutcome Parse(ISetupFileSystem fileSystem, string configPath)
    {
        var read = SetupNonInteractiveConfigReader.Read(fileSystem, configPath);
        if (!read.Succeeded)
        {
            return new ParseOutcome { Succeeded = false, FailureCode = read.FailureCode };
        }

        var json = SetupNonInteractiveConfigReader.DecodeUtf8(read.Content, out var validUtf8);
        if (!validUtf8)
        {
            return new ParseOutcome
            {
                Succeeded = false,
                FailureCode = SetupNonInteractiveResultCode.InvalidUtf8,
            };
        }

        if (!SetupNonInteractiveInputValidator.TryParse(json, out var input, out var failure))
        {
            return new ParseOutcome
            {
                Succeeded = false,
                Failure = failure,
                FailureCode = failure?.Code ?? SetupNonInteractiveResultCode.InvalidJson,
            };
        }

        return new ParseOutcome { Succeeded = true, Input = input };
    }
}

internal static class SetupNonInteractiveOrchestratorAdapter
{
    internal static SetupAssistantMainWorkflowState BuildInitialState(SetupNonInteractiveInput input) =>
        SetupAssistantMainWorkflowState.CreateInitial(input.Mode);

    internal static SetupAssistantMainCollectedInput BuildCollectedInput(SetupNonInteractiveInput input) =>
        new()
        {
            MainSetupInput = BuildMainSetupInput(input),
            TenantId = input.TenantId,
            StagingRecipientEmail = input.StagingRecipientEmail,
            StagingEnvironmentConfirmation = input.EnvironmentConfirmation,
            StagingIntentConfirmation = input.StagingIntentConfirmation,
            AssistantSessionId = $"non-interactive-{Guid.NewGuid():N}",
            ProductionEnvironmentConfirmation = input.EnvironmentConfirmation,
            LiveSendingEnableApproval = input.LiveSendingEnableApproval,
        };

    internal static SetupNonInteractiveResult FromOrchestrator(
        SetupMode mode,
        SetupAssistantMainWorkflowTransition result)
    {
        var wireMode = SetupModeParser.ToWireValue(mode);
        return new SetupNonInteractiveResult
        {
            Ok = result.State.IsComplete,
            Code = SetupAssistantResultPresenter.SafeCode(result.Code),
            Kind = SetupNonInteractiveKindWire.FromAssistantKind(result.Kind),
            Mode = wireMode,
            ConfigurationApplied = result.State.ConfigurationStageSucceeded,
            DeploymentSendReady = result.State.DeploymentSendReady,
            AdminBootstrapPerformed = false,
            BundleId = result.BundleId ?? result.State.MainSetup?.BundleId,
            ActionCode = string.IsNullOrEmpty(result.ActionCode) ? null : result.ActionCode,
            MainSetupStatus = result.State.IsComplete ? "succeeded" : "failed",
        };
    }

    private static SetupAssistantMainSetupInput BuildMainSetupInput(SetupNonInteractiveInput input)
    {
        var mode = input.Mode;
        var tokenEnv = SetupAssistantInputs.TokenEnvFor(mode);
        var tenants = new MailerTenantsFile
        {
            Version = 1,
            Environment = SetupAssistantInputs.EnvironmentFor(mode),
            Tenants =
            [
                new MailerTenant
                {
                    TenantId = input.TenantId,
                    Name = input.TenantName,
                    SourceServices = [input.SourceService],
                    DefaultFrom = new MailerAddress
                    {
                        Email = input.SenderEmail,
                        DisplayName = input.SenderDisplayName,
                    },
                    TokenEnv = tokenEnv,
                    Provider = SetupAssistantInputs.ProviderFor(mode),
                    LiveSending = false,
                    Retry = new MailerRetryOptions
                    {
                        MaxAttempts = 5,
                        InitialDelaySeconds = 5,
                        MaxDelaySeconds = 300,
                    },
                },
            ],
        };

        return new SetupAssistantMainSetupInput
        {
            Mode = mode,
            Tenants = tenants,
            TokenSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [tokenEnv] = input.ServiceToken,
            },
            AcsConnectionString = input.AcsConnectionString,
            AcsConnectionStringConfirmation = input.AcsConnectionString,
            PlatformSender = mode == SetupMode.LocalMailpit
                ? null
                : new SetupPlatformSenderInput
                {
                    Environment = SetupAssistantInputs.EnvironmentFor(mode),
                    Email = input.SenderEmail,
                    DisplayName = input.SenderDisplayName,
                },
            EnvironmentConfirmation = input.EnvironmentConfirmation ?? string.Empty,
            IntentConfirmation = input.IntentConfirmation ?? string.Empty,
        };
    }
}
