using System.Net.Mail;
using System.Text.Json.Serialization;

namespace Amane.Mailer.Configuration;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MailerTenantsFile
{
    private static readonly HashSet<string> AllowedEnvironments =
        ["develop", "staging", "production", "shared"];

    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    [JsonPropertyName("tenants")]
    public required IReadOnlyList<MailerTenant> Tenants { get; init; }

    public void Validate()
    {
        if (Version != 1)
        {
            throw new InvalidOperationException("Mailer tenant configuration version must be 1.");
        }

        if (!AllowedEnvironments.Contains(Environment))
        {
            throw new InvalidOperationException(
                "Mailer tenant configuration environment must be 'develop', 'staging', 'production', or 'shared'.");
        }

        if (Tenants.Count == 0)
        {
            throw new InvalidOperationException("Mailer tenant configuration must include at least one tenant.");
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MailerTenant
{
    private ISet<string>? _sourceServiceSet;

    [JsonPropertyName("tenant_id")]
    public required Guid TenantId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("source_services")]
    public required IReadOnlyList<string> SourceServices { get; init; }

    [JsonPropertyName("default_from")]
    public required MailerAddress DefaultFrom { get; init; }

    [JsonPropertyName("token_env")]
    public required string TokenEnv { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("live_sending")]
    public bool LiveSending { get; init; }

    [JsonPropertyName("metadata_max_bytes")]
    public int MetadataMaxBytes { get; init; } = 4096;

    [JsonPropertyName("retry")]
    public required MailerRetryOptions Retry { get; init; }

    public bool IsSourceServiceAllowed(string sourceService)
    {
        _sourceServiceSet ??= SourceServices.ToHashSet(StringComparer.Ordinal);
        return _sourceServiceSet.Contains(sourceService);
    }

    public void Validate()
    {
        if (TenantId == Guid.Empty)
        {
            throw new InvalidOperationException("tenant_id must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException("tenant name is required.");
        }

        if (SourceServices.Count == 0 || SourceServices.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException($"tenant '{Name}' must define at least one source_service.");
        }

        if (SourceServices.Distinct(StringComparer.Ordinal).Count() != SourceServices.Count)
        {
            throw new InvalidOperationException($"tenant '{Name}' source_services must be unique.");
        }

        foreach (var sourceService in SourceServices)
        {
            if (!IsValidSourceService(sourceService))
            {
                throw new InvalidOperationException(
                    $"tenant '{Name}' source_service '{sourceService}' must match ^[a-z0-9][a-z0-9_-]{{1,62}}$.");
            }
        }

        if (!MailAddress.TryCreate(DefaultFrom.Email, out _))
        {
            throw new InvalidOperationException($"tenant '{Name}' default_from.email is not a valid email address.");
        }

        if (DefaultFrom.DisplayName is { Length: 0 })
        {
            throw new InvalidOperationException($"tenant '{Name}' default_from.display_name must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(TokenEnv))
        {
            throw new InvalidOperationException($"tenant '{Name}' token_env is required.");
        }

        if (!IsValidTokenEnv(TokenEnv))
        {
            throw new InvalidOperationException(
                $"tenant '{Name}' token_env must match ^[A-Z][A-Z0-9_]*$.");
        }

        if (!Provider.Equals("mailpit", StringComparison.Ordinal)
            && !Provider.Equals("acs", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"tenant '{Name}' provider must be 'mailpit' or 'acs'.");
        }

        if (MetadataMaxBytes <= 0)
        {
            throw new InvalidOperationException($"tenant '{Name}' metadata_max_bytes must be positive.");
        }

        Retry.Validate(Name);
    }

    private static bool IsValidSourceService(string sourceService)
    {
        if (sourceService.Length is < 2 or > 63)
            return false;

        if (!IsAsciiLowerLetterOrDigit(sourceService[0]))
            return false;

        return sourceService[1..].All(static c =>
            IsAsciiLowerLetterOrDigit(c) || c is '_' or '-');
    }

    private static bool IsValidTokenEnv(string tokenEnv)
    {
        if (tokenEnv.Length == 0 || tokenEnv[0] is < 'A' or > 'Z')
            return false;

        return tokenEnv[1..].All(static c =>
            c is >= 'A' and <= 'Z'
            || c is >= '0' and <= '9'
            || c == '_');
    }

    private static bool IsAsciiLowerLetterOrDigit(char c) =>
        c is >= 'a' and <= 'z' || c is >= '0' and <= '9';
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MailerAddress
{
    [JsonPropertyName("email")]
    public required string Email { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MailerRetryOptions
{
    public const int MaxAttemptsUpperBound = 50;

    [JsonPropertyName("max_attempts")]
    public int MaxAttempts { get; init; }

    [JsonPropertyName("initial_delay_seconds")]
    public int InitialDelaySeconds { get; init; }

    [JsonPropertyName("max_delay_seconds")]
    public int MaxDelaySeconds { get; init; } = 300;

    public void Validate(string tenantName)
    {
        if (MaxAttempts <= 0)
        {
            throw new InvalidOperationException($"tenant '{tenantName}' retry.max_attempts must be positive.");
        }

        if (MaxAttempts > MaxAttemptsUpperBound)
        {
            throw new InvalidOperationException(
                $"tenant '{tenantName}' retry.max_attempts must be less than or equal to {MaxAttemptsUpperBound}.");
        }

        if (InitialDelaySeconds <= 0)
        {
            throw new InvalidOperationException($"tenant '{tenantName}' retry.initial_delay_seconds must be positive.");
        }

        if (MaxDelaySeconds <= 0)
        {
            throw new InvalidOperationException($"tenant '{tenantName}' retry.max_delay_seconds must be positive.");
        }
    }
}
