using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Configuration;

namespace Amane.Mailer.Setup;

/// <summary>
/// Deterministic canonical payload over normalized non-secret configuration only.
/// Bundle ID, created_at, transaction ID, and all secrets are excluded.
/// </summary>
public static class SetupCanonicalPayload
{
    public static byte[] Build(
        SetupMode mode,
        MailerTenantsFile tenants,
        IReadOnlyDictionary<string, string> composeEnv,
        PlatformSenderFile? platformSender,
        bool adminBootstrapRequested)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SetupBundleLayout.RecordedSchemaVersion);
            writer.WriteString("mode", SetupModeParser.ToWireValue(mode));

            writer.WritePropertyName("tenants");
            WriteTenants(writer, tenants);

            writer.WritePropertyName("composeEnv");
            WriteSortedStringMap(writer, composeEnv);

            if (platformSender is not null)
            {
                writer.WritePropertyName("platformSender");
                WritePlatformSender(writer, platformSender);
            }

            writer.WriteBoolean("adminBootstrapRequested", adminBootstrapRequested);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static string FingerprintSha256(ReadOnlySpan<byte> canonicalPayload)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(canonicalPayload, hash);
        var sb = new StringBuilder(7 + (hash.Length * 2));
        sb.Append("sha256:");
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }

    private static void WriteTenants(Utf8JsonWriter writer, MailerTenantsFile tenants)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", tenants.Version);
        writer.WriteString("environment", tenants.Environment);
        writer.WritePropertyName("tenants");
        writer.WriteStartArray();

        foreach (var tenant in tenants.Tenants.OrderBy(t => t.TenantId))
        {
            writer.WriteStartObject();
            writer.WriteString("tenant_id", tenant.TenantId);
            writer.WriteString("name", tenant.Name);
            writer.WritePropertyName("source_services");
            writer.WriteStartArray();
            foreach (var service in tenant.SourceServices.OrderBy(s => s, StringComparer.Ordinal))
            {
                writer.WriteStringValue(service);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("default_from");
            writer.WriteStartObject();
            writer.WriteString("email", tenant.DefaultFrom.Email);
            if (tenant.DefaultFrom.DisplayName is not null)
            {
                writer.WriteString("display_name", tenant.DefaultFrom.DisplayName);
            }

            writer.WriteEndObject();
            writer.WriteString("token_env", tenant.TokenEnv);
            writer.WriteString("provider", tenant.Provider);
            writer.WriteBoolean("live_sending", tenant.LiveSending);
            writer.WriteNumber("metadata_max_bytes", tenant.MetadataMaxBytes);
            writer.WritePropertyName("retry");
            writer.WriteStartObject();
            writer.WriteNumber("max_attempts", tenant.Retry.MaxAttempts);
            writer.WriteNumber("initial_delay_seconds", tenant.Retry.InitialDelaySeconds);
            writer.WriteNumber("max_delay_seconds", tenant.Retry.MaxDelaySeconds);
            writer.WriteEndObject();
            if (tenant.Webhook is not null)
            {
                writer.WritePropertyName("webhook");
                writer.WriteStartObject();
                writer.WriteString("url", tenant.Webhook.Url);
                writer.WriteString("secret_env", tenant.Webhook.SecretEnv);

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WritePlatformSender(Utf8JsonWriter writer, PlatformSenderFile sender)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", sender.Version);
        writer.WriteString("environment", sender.Environment);
        writer.WriteString("provider", sender.Provider);
        writer.WriteBoolean("live_sending", sender.LiveSending);
        writer.WritePropertyName("sender");
        writer.WriteStartObject();
        writer.WriteString("email", sender.Sender.Email);
        writer.WriteString("display_name", sender.Sender.DisplayName);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteSortedStringMap(Utf8JsonWriter writer, IReadOnlyDictionary<string, string> map)
    {
        writer.WriteStartObject();
        foreach (var pair in map.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            writer.WriteString(pair.Key, pair.Value);
        }

        writer.WriteEndObject();
    }
}
