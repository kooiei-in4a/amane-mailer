using System.Net;
using System.Text.RegularExpressions;
using Amane.Mailer.Operations.AcsSetup;

namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// Shape checks applied before any typed operation is called. These are UI guards only: the
/// authoritative validation stays in Setup Core (#448) and the typed ACS/Admin workflows.
/// </summary>
internal static partial class SetupAssistantInputs
{
    internal const int MinSecretLength = 16;
    internal const int MaxFieldLength = 320;

    /// <summary>Every mode the assistant can drive. Mode 5 is deliberately absent.</summary>
    internal static bool TryParseAutomatableMode(string? raw, out SetupMode mode)
    {
        mode = default;
        return SetupModeParser.TryParse(raw, out mode)
            && mode is SetupMode.LocalMailpit
                or SetupMode.StagingNoSend
                or SetupMode.StagingVerification
                or SetupMode.ProductionAcs;
    }

    internal const string ManualModeValue = "production-queue";

    internal static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 64
        && IdentifierPattern().IsMatch(value);

    /// <summary>Mirrors the tenant <c>source_services</c> shape enforced by the tenant schema.</summary>
    internal static bool IsSourceService(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length is >= 2 and <= 63
        && SourceServicePattern().IsMatch(value);

    internal static bool IsEmail(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaxFieldLength
        && EmailPattern().IsMatch(value);

    internal static bool IsDisplayText(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && !value.Any(static c => char.IsControl(c));

    internal static bool IsIpAddress(string? value) =>
        !string.IsNullOrWhiteSpace(value) && IPAddress.TryParse(value, out _);

    internal static bool IsAbsoluteOrigin(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaxFieldLength
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    internal static bool IsSecret(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length >= MinSecretLength
        && value.Length <= 4096
        && !value.Any(static c => char.IsControl(c));

    /// <summary>The token env key is derived from the mode; the operator cannot supply one.</summary>
    internal static string TokenEnvFor(SetupMode mode) => mode switch
    {
        SetupMode.LocalMailpit => "MAIL_SERVICE_TOKEN_DEVELOP",
        SetupMode.StagingNoSend or SetupMode.StagingVerification => "MAIL_SERVICE_TOKEN_STAGING",
        SetupMode.ProductionAcs => "MAIL_SERVICE_TOKEN_PRODUCTION",
        _ => "MAIL_SERVICE_TOKEN",
    };

    internal static string ProviderFor(SetupMode mode) =>
        mode == SetupMode.LocalMailpit ? "mailpit" : "acs";

    internal static string EnvironmentFor(SetupMode mode) =>
        SetupRequestValidator.ExpectedEnvironment(mode);

    /// <summary>Exact phrase the operator must retype for the mode, per the #451 contract.</summary>
    internal static string EnvironmentConfirmationFor(SetupMode mode) =>
        mode == SetupMode.ProductionAcs
            ? AcsEnvironmentConfirmation.Production
            : AcsEnvironmentConfirmation.Staging;

    internal static string Mask(string? email) => AcsAddressMask.MaskEmail(email ?? string.Empty);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SourceServicePattern();

    [GeneratedRegex(@"^[^@\s]{1,64}@[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();
}
