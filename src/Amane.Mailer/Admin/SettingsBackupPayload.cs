using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amane.Mailer.Identity;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Admin;

internal static class SettingsBackupFormat
{
    internal const string FormatIdentifier = "amane-mailer-settings";
    internal const int CurrentVersion = 1;
    internal const int MaxPlaintextBytes = 1024 * 1024;
    internal const int MinPassphraseLength = 12;
    internal const int MaxPassphraseLength = 1024;

    internal static bool IsValidPassphrase(string? passphrase) =>
        !string.IsNullOrWhiteSpace(passphrase)
        && passphrase.Length is >= MinPassphraseLength and <= MaxPassphraseLength;

    internal static byte[] Serialize(SettingsBackupPayload payload) =>
        JsonSerializer.SerializeToUtf8Bytes(
            payload,
            SettingsBackupJsonContext.Default.SettingsBackupPayload);

    internal static bool TryDeserializeAndValidate(
        ReadOnlySpan<byte> json,
        out SettingsBackupPayload? validated)
    {
        validated = null;
        if (json.Length is 0 or > MaxPlaintextBytes)
            return false;

        try
        {
            var candidate = JsonSerializer.Deserialize(
                json,
                SettingsBackupJsonContext.Default.SettingsBackupPayload);
            if (candidate is null
                || !string.Equals(candidate.Format, FormatIdentifier, StringComparison.Ordinal)
                || candidate.Version != CurrentVersion
                || !DateTimeOffset.TryParseExact(
                    candidate.ExportedAtUtc,
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out _)
                || candidate.Settings is null
                || candidate.Settings.Provider is null
                || !string.Equals(candidate.Settings.Provider.Type, "acs", StringComparison.Ordinal)
                || candidate.Settings.GoogleLogin is null
                || candidate.Settings.Senders is null
                || candidate.Secrets is null
                || string.IsNullOrWhiteSpace(candidate.Secrets.AcsConnectionString)
                || !FirstRunSetupStorage.IsValidAcsConnectionString(candidate.Secrets.AcsConnectionString))
            {
                return false;
            }

            var google = candidate.Settings.GoogleLogin;
            var googleClientId = string.IsNullOrWhiteSpace(google.ClientId)
                ? null
                : google.ClientId.Trim();
            if (googleClientId is { Length: > 512 }
                || (googleClientId?.Any(char.IsControl) ?? false))
            {
                return false;
            }

            if (!google.Included)
            {
                if (google.Enabled
                    || google.ClientId is not null
                    || candidate.Secrets.GoogleClientSecret is not null)
                {
                    return false;
                }
            }
            else
            {
                if (candidate.Secrets.GoogleClientSecret is { } googleSecret
                    && string.IsNullOrWhiteSpace(googleSecret))
                {
                    return false;
                }

                if (google.Enabled
                    && (string.IsNullOrWhiteSpace(googleClientId)
                        || string.IsNullOrWhiteSpace(candidate.Secrets.GoogleClientSecret)))
                {
                    return false;
                }
            }

            var senders = new SettingsBackupSender[candidate.Settings.Senders.Length];
            var emails = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < candidate.Settings.Senders.Length; index++)
            {
                var sender = candidate.Settings.Senders[index];
                if (sender is null || string.IsNullOrWhiteSpace(sender.Email))
                    return false;

                string normalizedEmail;
                try
                {
                    normalizedEmail = SenderRepository.NormalizeEmail(sender.Email);
                }
                catch (ArgumentException)
                {
                    return false;
                }

                if (!emails.Add(normalizedEmail))
                    return false;

                var displayName = string.IsNullOrWhiteSpace(sender.DisplayName)
                    ? null
                    : sender.DisplayName.Trim();
                if (displayName is { Length: > 200 }
                    || (displayName?.Any(char.IsControl) ?? false))
                {
                    return false;
                }

                senders[index] = new SettingsBackupSender
                {
                    Email = normalizedEmail,
                    DisplayName = displayName,
                    Enabled = sender.Enabled,
                };
            }

            validated = new SettingsBackupPayload
            {
                Format = candidate.Format,
                Version = candidate.Version,
                ExportedAtUtc = candidate.ExportedAtUtc,
                Settings = new SettingsBackupSettings
                {
                    Provider = new SettingsBackupProvider { Type = candidate.Settings.Provider.Type },
                    LiveSending = candidate.Settings.LiveSending,
                    GoogleLogin = new SettingsBackupGoogleLogin
                    {
                        Included = google.Included,
                        Enabled = google.Enabled,
                        ClientId = google.Included ? googleClientId : null,
                    },
                    Senders = senders,
                },
                Secrets = new SettingsBackupSecrets
                {
                    AcsConnectionString = candidate.Secrets.AcsConnectionString.Trim(),
                    GoogleClientSecret = google.Included
                        ? candidate.Secrets.GoogleClientSecret?.Trim()
                        : null,
                },
            };
            return true;
        }
        catch (Exception ex) when (ex is JsonException
            or NotSupportedException
            or ArgumentException
            or InvalidOperationException)
        {
            return false;
        }
    }
}

internal sealed class SettingsBackupPayload
{
    public required string Format { get; init; }

    public required int Version { get; init; }

    public required string ExportedAtUtc { get; init; }

    public required SettingsBackupSettings Settings { get; init; }

    public required SettingsBackupSecrets Secrets { get; init; }
}

internal sealed class SettingsBackupSettings
{
    public required SettingsBackupProvider Provider { get; init; }

    public required bool LiveSending { get; init; }

    public required SettingsBackupGoogleLogin GoogleLogin { get; init; }

    public required SettingsBackupSender[] Senders { get; init; }
}

internal sealed class SettingsBackupProvider
{
    public required string Type { get; init; }
}

internal sealed class SettingsBackupGoogleLogin
{
    public required bool Included { get; init; }

    public required bool Enabled { get; init; }

    public required string? ClientId { get; init; }
}

internal sealed class SettingsBackupSender
{
    public required string Email { get; init; }

    public required string? DisplayName { get; init; }

    public required bool Enabled { get; init; }
}

internal sealed class SettingsBackupSecrets
{
    public required string AcsConnectionString { get; init; }

    public required string? GoogleClientSecret { get; init; }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SettingsBackupPayload))]
internal partial class SettingsBackupJsonContext : JsonSerializerContext
{
}
