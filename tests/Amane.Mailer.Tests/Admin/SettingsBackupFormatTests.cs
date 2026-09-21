using System.Text;
using Amane.Mailer.Admin;

namespace Amane.Mailer.Tests.Admin;

public sealed class SettingsBackupFormatTests
{
    [Fact]
    public void V1_payload_is_source_generated_and_normalizes_sender_identity()
    {
        var json = SettingsBackupFormat.Serialize(CreatePayload(
            senders: [CreateSender(" Sender@Example.com ", "Example")]));

        Assert.True(SettingsBackupFormat.TryDeserializeAndValidate(json, out var payload));
        Assert.NotNull(payload);
        Assert.Equal(SettingsBackupFormat.FormatIdentifier, payload.Format);
        Assert.Equal(SettingsBackupFormat.CurrentVersion, payload.Version);
        Assert.Equal("sender@example.com", Assert.Single(payload.Settings.Senders).Email);
        Assert.Contains("\"format\"", Encoding.UTF8.GetString(json), StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_wrong_format_newer_version_malformed_json_and_unknown_members()
    {
        AssertRejected(CreatePayload(format: "other-format"));
        AssertRejected(CreatePayload(version: SettingsBackupFormat.CurrentVersion + 1));
        Assert.False(SettingsBackupFormat.TryDeserializeAndValidate("{"u8, out _));

        var json = Encoding.UTF8.GetString(SettingsBackupFormat.Serialize(CreatePayload()));
        var withUnknownMember = json.Replace(
            "\"format\":",
            "\"unexpectedSecretField\":\"must-not-be-accepted\",\n  \"format\":",
            StringComparison.Ordinal);
        Assert.False(SettingsBackupFormat.TryDeserializeAndValidate(
            Encoding.UTF8.GetBytes(withUnknownMember),
            out _));
    }

    [Fact]
    public void Rejects_duplicate_normalized_sender_emails_and_invalid_sender_names()
    {
        AssertRejected(CreatePayload(senders:
        [
            CreateSender("first@example.com", "First"),
            CreateSender(" FIRST@example.com ", "Duplicate"),
        ]));

        AssertRejected(CreatePayload(senders:
        [
            CreateSender("sender@example.com", new string('x', 201)),
        ]));
    }

    [Fact]
    public void Legacy_google_authority_must_not_carry_managed_credentials()
    {
        var payload = CreatePayload();
        payload = new SettingsBackupPayload
        {
            Format = payload.Format,
            Version = payload.Version,
            ExportedAtUtc = payload.ExportedAtUtc,
            Settings = new SettingsBackupSettings
            {
                Provider = payload.Settings.Provider,
                LiveSending = payload.Settings.LiveSending,
                GoogleLogin = new SettingsBackupGoogleLogin
                {
                    Included = false,
                    Enabled = false,
                    ClientId = null,
                },
                Senders = payload.Settings.Senders,
            },
            Secrets = new SettingsBackupSecrets
            {
                AcsConnectionString = payload.Secrets.AcsConnectionString,
                GoogleClientSecret = null,
            },
        };

        var json = SettingsBackupFormat.Serialize(payload);
        Assert.True(SettingsBackupFormat.TryDeserializeAndValidate(json, out var validated));
        Assert.NotNull(validated);
        Assert.False(validated.Settings.GoogleLogin.Included);
        Assert.Null(validated.Secrets.GoogleClientSecret);

        var invalidPayload = new SettingsBackupPayload
        {
            Format = payload.Format,
            Version = payload.Version,
            ExportedAtUtc = payload.ExportedAtUtc,
            Settings = payload.Settings,
            Secrets = new SettingsBackupSecrets
            {
                AcsConnectionString = payload.Secrets.AcsConnectionString,
                GoogleClientSecret = "test-only-google-secret",
            },
        };
        AssertRejected(invalidPayload);
    }

    [Fact]
    public void Enabled_managed_google_login_requires_client_id_and_secret()
    {
        var payload = CreatePayload(googleEnabled: true, googleSecret: null);
        AssertRejected(payload);
    }

    private static void AssertRejected(SettingsBackupPayload payload) =>
        Assert.False(SettingsBackupFormat.TryDeserializeAndValidate(
            SettingsBackupFormat.Serialize(payload),
            out _));

    internal static SettingsBackupPayload CreatePayload(
        string format = SettingsBackupFormat.FormatIdentifier,
        int version = SettingsBackupFormat.CurrentVersion,
        bool googleIncluded = true,
        bool googleEnabled = false,
        string? googleSecret = "test-only-google-secret",
        SettingsBackupSender[]? senders = null) =>
        new()
        {
            Format = format,
            Version = version,
            ExportedAtUtc = "2026-09-21T00:00:00Z",
            Settings = new SettingsBackupSettings
            {
                Provider = new SettingsBackupProvider { Type = "acs" },
                LiveSending = true,
                GoogleLogin = new SettingsBackupGoogleLogin
                {
                    Included = googleIncluded,
                    Enabled = googleEnabled,
                    ClientId = googleIncluded ? "test-only.apps.exampleusercontent.com" : null,
                },
                Senders = senders ?? [CreateSender("sender@example.com", "Sender")],
            },
            Secrets = new SettingsBackupSecrets
            {
                AcsConnectionString = "Endpoint=https://example.communication.azure.com/;AccessKey=abc123",
                GoogleClientSecret = googleIncluded ? googleSecret : null,
            },
        };

    private static SettingsBackupSender CreateSender(string email, string? displayName) =>
        new()
        {
            Email = email,
            DisplayName = displayName,
            Enabled = true,
        };
}
