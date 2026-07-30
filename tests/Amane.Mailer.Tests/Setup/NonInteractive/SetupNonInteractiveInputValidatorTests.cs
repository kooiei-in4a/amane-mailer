using Amane.Mailer.Operations.AcsSetup;
using Amane.Mailer.Setup;
using Amane.Mailer.Setup.NonInteractive;

namespace Amane.Mailer.Tests.Setup.NonInteractive;

public sealed class SetupNonInteractiveInputValidatorTests
{
    [Fact]
    public void Missing_tenantId_is_rejected()
    {
        var json = """
            {
              "schemaVersion": 1,
              "mode": "local-mailpit",
              "tenant": {
                "tenantName": "example-develop",
                "sourceService": "example-service",
                "senderEmail": "noreply@example.com",
                "senderDisplayName": "Example Service"
              },
              "serviceToken": "synthetic-mail-token-not-real"
            }
            """;

        Assert.False(SetupNonInteractiveInputValidator.TryParse(json, out _, out var failure));
        Assert.Equal(SetupNonInteractiveResultCode.MissingRequiredField, failure!.Code);
    }

    [Fact]
    public void Duplicate_tenantId_property_is_rejected()
    {
        var json = """
            {
              "schemaVersion": 1,
              "mode": "local-mailpit",
              "tenant": {
                "tenantId": "00000000-0000-0000-0000-000000000101",
                "tenantId": "00000000-0000-0000-0000-000000000102",
                "tenantName": "example-develop",
                "sourceService": "example-service",
                "senderEmail": "noreply@example.com",
                "senderDisplayName": "Example Service"
              },
              "serviceToken": "synthetic-mail-token-not-real"
            }
            """;

        Assert.False(SetupNonInteractiveInputValidator.TryParse(json, out _, out var failure));
        Assert.Equal(SetupNonInteractiveResultCode.DuplicateProperty, failure!.Code);
    }

    [Fact]
    public void Malformed_json_is_rejected()
    {
        var configPath = SetupNonInteractiveTestSupport.WriteOwnerOnlyConfigOnHost(
            Path.Combine(Path.GetTempPath(), "amane-bad-" + Guid.NewGuid().ToString("N") + ".json"),
            "{not-json");
        var outcome = SetupNonInteractiveInputParser.Parse(new HostSetupFileSystem(), configPath);
        Assert.False(outcome.Succeeded);
        Assert.Equal(SetupNonInteractiveResultCode.InvalidJson, outcome.FailureCode);
    }

    [Fact]
    public void Unsupported_schema_version_is_rejected()
    {
        var json = SetupNonInteractiveTestSupport.BuildLocalMailpitJson()
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);

        Assert.False(SetupNonInteractiveInputValidator.TryParse(json, out _, out var failure));
        Assert.Equal(SetupNonInteractiveResultCode.UnsupportedSchema, failure!.Code);
    }

    [Fact]
    public void Unknown_root_field_is_rejected()
    {
        var json = SetupNonInteractiveTestSupport.BuildLocalMailpitJson()
            .Replace(
                "\"serviceToken\"",
                "\"unexpectedField\": \"value\",\n  \"serviceToken\"",
                StringComparison.Ordinal);

        Assert.False(SetupNonInteractiveInputValidator.TryParse(json, out _, out var failure));
        Assert.Equal(SetupNonInteractiveResultCode.UnknownField, failure!.Code);
    }

    [Fact]
    public void Nested_admin_key_with_action_is_rejected()
    {
        var json = """
            {
              "schemaVersion": 1,
              "mode": "local-mailpit",
              "admin": { "action": "bootstrap" },
              "tenant": {
                "tenantId": "00000000-0000-0000-0000-000000000101",
                "tenantName": "example-develop",
                "sourceService": "example-service",
                "senderEmail": "noreply@example.com",
                "senderDisplayName": "Example Service"
              },
              "serviceToken": "synthetic-mail-token-not-real"
            }
            """;

        Assert.False(SetupNonInteractiveInputValidator.TryParse(json, out _, out var failure));
        Assert.Equal(SetupNonInteractiveResultCode.AdminInputRejected, failure!.Code);
        Assert.Equal(SetupNonInteractiveResultCode.UseSetupAssistantAction, failure.ActionCode);
    }

    [Fact]
    public void Admin_username_field_is_rejected()
    {
        var json = SetupNonInteractiveTestSupport.BuildLocalMailpitJson()
            .Replace(
                "\"serviceToken\"",
                "\"adminUsername\": \"local-admin\",\n  \"serviceToken\"",
                StringComparison.Ordinal);

        Assert.False(SetupNonInteractiveInputValidator.TryParse(json, out _, out var failure));
        Assert.Equal(SetupNonInteractiveResultCode.AdminInputRejected, failure!.Code);
    }

    [Fact]
    public void Forbidden_acs_field_in_mailpit_mode_is_rejected()
    {
        var json = SetupNonInteractiveTestSupport.BuildLocalMailpitJson()
            .Replace(
                "\"serviceToken\"",
                "\"acsConnectionString\": \"endpoint=https://synthetic.example/;accesskey=SYNTHETIC000000000000000000000000000000000000=\",\n  \"serviceToken\"",
                StringComparison.Ordinal);

        Assert.False(SetupNonInteractiveInputValidator.TryParse(json, out _, out var failure));
        Assert.Equal(SetupNonInteractiveResultCode.ForbiddenField, failure!.Code);
    }

    [Fact]
    public void Mode_5_is_rejected()
    {
        var json = SetupNonInteractiveTestSupport.BuildLocalMailpitJson()
            .Replace("\"local-mailpit\"", "\"production-queue\"", StringComparison.Ordinal);

        Assert.False(SetupNonInteractiveInputValidator.TryParse(json, out _, out var failure));
        Assert.Equal(SetupNonInteractiveResultCode.ModeNotSupported, failure!.Code);
        Assert.Equal("production-queue", failure.Mode);
    }

    [Fact]
    public void Duplicate_root_property_is_rejected()
    {
        var json = """
            {
              "schemaVersion": 1,
              "schemaVersion": 1,
              "mode": "local-mailpit",
              "tenant": {
                "tenantId": "00000000-0000-0000-0000-000000000101",
                "tenantName": "example-develop",
                "sourceService": "example-service",
                "senderEmail": "noreply@example.com",
                "senderDisplayName": "Example Service"
              },
              "serviceToken": "synthetic-mail-token-not-real"
            }
            """;

        Assert.False(SetupNonInteractiveInputValidator.TryParse(json, out _, out var failure));
        Assert.Equal(SetupNonInteractiveResultCode.DuplicateProperty, failure!.Code);
    }

    [Fact]
    public void Staging_verification_requires_exact_staging_phrases()
    {
        var json = SetupNonInteractiveTestSupport.BuildStagingVerificationJson()
            .Replace("\"MAILER-ACS-TEST-SEND\"", "\"WRONG-PHRASE\"", StringComparison.Ordinal);

        Assert.False(SetupNonInteractiveInputValidator.TryParse(json, out _, out var failure));
        Assert.Equal(SetupNonInteractiveResultCode.ConfirmationPhraseMismatch, failure!.Code);
        Assert.Equal("staging-verification", failure.Mode);
    }

    [Fact]
    public void Valid_local_mailpit_input_parses()
    {
        var json = SetupNonInteractiveTestSupport.BuildLocalMailpitJson();
        Assert.True(SetupNonInteractiveInputValidator.TryParse(json, out var input, out _));
        Assert.Equal(SetupMode.LocalMailpit, input!.Mode);
        Assert.Equal(SetupNonInteractiveTestSupport.SyntheticServiceToken, input.ServiceToken);
    }
}
