using System.Net.Mail;
using System.Text.Json.Serialization;

namespace Amane.Mailer.Configuration;

/// <summary>
/// Sender identity for platform-owned (tenant-less) mail such as System Admin confirmation mail.
/// Written by <c>admin provider register-acs</c>. Not consumed by any runtime send path yet;
/// wiring this into a send decision belongs to the platform-owned mail request contract
/// (tracked separately, out of scope here).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlatformSenderFile
{
    /// <summary>
    /// Canonical file name used everywhere this file is referenced: schema, code, Compose
    /// comments, runbooks, and tests. Do not hardcode the string name anywhere else.
    /// </summary>
    public const string CanonicalFileName = "platform-sender.json";

    private static readonly HashSet<string> AllowedEnvironments = ["staging"];
    private static readonly HashSet<string> AllowedProviders = ["acs"];

    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    [JsonPropertyName("sender")]
    public required PlatformSenderAddress Sender { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("live_sending")]
    public bool LiveSending { get; init; }

    public void Validate()
    {
        if (Version != 1)
        {
            throw new InvalidOperationException("Platform sender configuration version must be 1.");
        }

        if (!AllowedEnvironments.Contains(Environment))
        {
            throw new InvalidOperationException(
                "Platform sender configuration environment must be 'staging'.");
        }

        if (!AllowedProviders.Contains(Provider))
        {
            throw new InvalidOperationException("Platform sender configuration provider must be 'acs'.");
        }

        if (LiveSending)
        {
            throw new InvalidOperationException(
                "Platform sender configuration live_sending must be false. Enabling live sending requires a separate approved action.");
        }

        Sender.Validate();
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlatformSenderAddress
{
    [JsonPropertyName("email")]
    public required string Email { get; init; }

    [JsonPropertyName("display_name")]
    public required string DisplayName { get; init; }

    public void Validate()
    {
        if (!MailAddress.TryCreate(Email, out var parsed) || !string.Equals(parsed.Address, Email, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("sender.email must be a bare email address.");
        }

        if (string.IsNullOrEmpty(DisplayName) || DisplayName.Length > 200)
        {
            throw new InvalidOperationException("sender.display_name must be 1-200 characters.");
        }

        if (DisplayName.Any(char.IsControl))
        {
            throw new InvalidOperationException("sender.display_name must not contain control characters.");
        }
    }
}
