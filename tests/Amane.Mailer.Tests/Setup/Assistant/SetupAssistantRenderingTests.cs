using Amane.Mailer.Setup;
using Amane.Mailer.Setup.Assistant;

namespace Amane.Mailer.Tests.Setup.Assistant;

/// <summary>
/// Input, encoding, and secret/PII boundary checks for the rendered screens (Issue #452).
/// </summary>
public sealed class SetupAssistantRenderingTests
{
    private const string XssPayload = "\"><script>alert('x')</script>";
    private const string ServiceToken = "assistant-test-service-token";
    private const string AcsConnectionString = "endpoint=https://example.test/;accesskey=not-a-real-key";

    [Fact]
    public async Task An_xss_payload_in_a_rejected_field_is_never_echoed_back()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await ReachTenantAsync(harness);

        await harness.PostStepAsync(
            "/tenant",
            ("tenant_name", XssPayload),
            ("source_service", "example-service"),
            ("sender_email", "no-reply@example.test"),
            ("sender_display_name", "Example"));

        var page = await harness.ReadCurrentPageAsync();

        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert('x')", page, StringComparison.Ordinal);
        Assert.DoesNotContain(XssPayload, page, StringComparison.Ordinal);
        Assert.Contains("英数字とハイフン", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accepted_operator_text_never_reaches_a_later_screen_in_any_form()
    {
        const string DisplayName = "A & B <Notifications> \"quoted\"";

        await using var harness = await SetupAssistantHarness.StartAsync();
        await ReachTenantAsync(harness);

        await harness.PostStepAsync(
            "/tenant",
            ("tenant_name", "example-tenant"),
            ("source_service", "example-service"),
            ("sender_email", "no-reply@example.test"),
            ("sender_display_name", DisplayName));
        var providerScreen = await harness.ReadCurrentPageAsync();

        await harness.PostStepAsync(
            "/provider",
            ("service_token", ServiceToken),
            ("service_token_confirm", ServiceToken));
        await harness.PostStepAsync(
            "/acs",
            ("acs_connection_string", AcsConnectionString),
            ("acs_connection_string_confirm", AcsConnectionString),
            ("platform_sender_display_name", "Example Platform"));
        var confirmationScreen = await harness.ReadCurrentPageAsync();

        foreach (var screen in new[] { providerScreen, confirmationScreen })
        {
            Assert.DoesNotContain(DisplayName, screen, StringComparison.Ordinal);
            Assert.DoesNotContain("<Notifications>", screen, StringComparison.Ordinal);
            Assert.DoesNotContain("&lt;Notifications&gt;", screen, StringComparison.Ordinal);
            Assert.DoesNotContain("example-tenant", screen, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_confirmation_screen_masks_the_sender_and_hides_every_secret()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await ReachConfirmationAsync(harness);

        var page = await harness.ReadCurrentPageAsync();

        Assert.DoesNotContain(ServiceToken, page, StringComparison.Ordinal);
        Assert.DoesNotContain(AcsConnectionString, page, StringComparison.Ordinal);
        Assert.DoesNotContain("accesskey", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no-reply@example.test", page, StringComparison.Ordinal);
        Assert.Contains("入力済み（表示しません）", page, StringComparison.Ordinal);
        Assert.Contains(
            SetupAssistantInputs.Mask("no-reply@example.test"),
            page,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refreshing_or_going_back_never_restores_a_secret_or_an_address()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await ReachConfirmationAsync(harness);

        // Re-reading the current screen is what a refresh and a browser back both produce,
        // because every state change is a POST followed by a redirect to GET /.
        var first = await harness.ReadCurrentPageAsync();
        var second = await harness.ReadCurrentPageAsync();

        foreach (var page in new[] { first, second })
        {
            Assert.DoesNotContain(ServiceToken, page, StringComparison.Ordinal);
            Assert.DoesNotContain(AcsConnectionString, page, StringComparison.Ordinal);
            Assert.DoesNotContain("value=\"no-reply@example.test\"", page, StringComparison.Ordinal);
            Assert.DoesNotContain("value=\"qa@example.test\"", page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Input_fields_never_carry_a_prefilled_value_attribute()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await ReachTenantAsync(harness);

        var page = await harness.ReadCurrentPageAsync();

        foreach (var line in page.Split('\n'))
        {
            if (line.Contains("<input", StringComparison.Ordinal)
                && !line.Contains("type=\"hidden\"", StringComparison.Ordinal)
                && !line.Contains("type=\"radio\"", StringComparison.Ordinal)
                && !line.Contains("type=\"checkbox\"", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("value=", line, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task No_screen_offers_a_host_path_docker_or_image_input()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await harness.RedeemTokenAsync();

        var screens = new List<string>();
        screens.Add(await harness.ReadCurrentPageAsync());
        await harness.PostStepAsync("/welcome");
        screens.Add(await harness.ReadCurrentPageAsync());
        await harness.PostStepAsync("/preflight");
        screens.Add(await harness.ReadCurrentPageAsync());
        await harness.PostStepAsync("/preflight", ("action", "continue"));
        screens.Add(await harness.ReadCurrentPageAsync());
        await harness.PostStepAsync("/mode", ("mode", "staging-no-send"));
        screens.Add(await harness.ReadCurrentPageAsync());

        foreach (var screen in screens)
        {
            foreach (var forbidden in new[]
                     {
                         "compose_file", "compose-file", "docker_args", "image_tag", "image_digest",
                         "volume", "service_name", "managed_root", "host_path", "command",
                         "environment_dictionary",
                     })
            {
                Assert.DoesNotContain($"name=\"{forbidden}\"", screen, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task An_unknown_result_code_falls_back_to_fixed_text_without_provider_detail()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        harness.Operations.DockerPreflight = new SetupAssistantDockerPreflightOutcome
        {
            Passed = false,
            Code = "provider.raw: Connection refused at /var/run/docker.sock (secret=abc)",
        };

        await harness.RedeemTokenAsync();
        await harness.PostStepAsync("/welcome");
        await harness.PostStepAsync("/preflight");

        var page = await harness.ReadCurrentPageAsync();

        Assert.Contains(SetupAssistantResultPresenter.UnknownResultText, page, StringComparison.Ordinal);
        Assert.Contains(SetupAssistantResultPresenter.UnrecognizedCode, page, StringComparison.Ordinal);
        Assert.DoesNotContain("Connection refused", page, StringComparison.Ordinal);
        Assert.DoesNotContain("/var/run/docker.sock", page, StringComparison.Ordinal);
        Assert.DoesNotContain("secret=abc", page, StringComparison.Ordinal);
    }

    [Fact]
    public void The_displayed_result_code_is_shape_guarded()
    {
        Assert.Equal(
            SetupApplyResultCode.ApplySucceeded,
            SetupAssistantResultPresenter.SafeCode(SetupApplyResultCode.ApplySucceeded));
        Assert.Equal("not-applicable", SetupAssistantResultPresenter.SafeCode("not-applicable"));

        foreach (var unsafeValue in new[]
                 {
                     "System.Exception: boom",
                     "/var/lib/amane/managed",
                     "C:\\managed\\state",
                     "endpoint=https://x.test/;accesskey=abc",
                     "<script>alert(1)</script>",
                     new string('a', 200),
                 })
        {
            Assert.Equal(
                SetupAssistantResultPresenter.UnrecognizedCode,
                SetupAssistantResultPresenter.SafeCode(unsafeValue));
        }
    }

    [Fact]
    public void The_result_presenter_never_returns_operator_supplied_text()
    {
        Assert.Equal(
            SetupAssistantResultPresenter.UnknownResultText,
            SetupAssistantResultPresenter.Describe("Exception: Object reference not set"));
        Assert.Equal(
            SetupAssistantResultPresenter.UnknownResultText,
            SetupAssistantResultPresenter.Describe(null));
        Assert.Equal(
            "適用前の Docker 確認に失敗しました。",
            SetupAssistantResultPresenter.Describe(SetupApplyResultCode.PreflightFailed));
    }

    [Fact]
    public void Masking_keeps_only_the_first_character_and_the_domain()
    {
        Assert.Equal("n***@example.test", SetupAssistantInputs.Mask("no-reply@example.test"));
        Assert.Equal("***", SetupAssistantInputs.Mask("not-an-address"));
        Assert.Equal("***", SetupAssistantInputs.Mask(null));
    }

    [Fact]
    public void Identifier_validation_rejects_path_and_shell_characters()
    {
        Assert.True(SetupAssistantInputs.IsIdentifier("example-tenant"));
        Assert.False(SetupAssistantInputs.IsIdentifier("../../etc/passwd"));
        Assert.False(SetupAssistantInputs.IsIdentifier("C:\\managed"));
        Assert.False(SetupAssistantInputs.IsIdentifier("tenant; rm -rf /"));
        Assert.False(SetupAssistantInputs.IsIdentifier("tenant name"));
        Assert.False(SetupAssistantInputs.IsIdentifier(string.Empty));
    }

    [Fact]
    public void Mode_parsing_never_accepts_the_manual_mode_as_automatable()
    {
        Assert.True(SetupAssistantInputs.TryParseAutomatableMode("local-mailpit", out var mailpit));
        Assert.Equal(SetupMode.LocalMailpit, mailpit);
        Assert.False(
            SetupAssistantInputs.TryParseAutomatableMode(SetupAssistantInputs.ManualModeValue, out _));
        Assert.False(SetupAssistantInputs.TryParseAutomatableMode("anything-else", out _));
    }

    // ------------------------------------------------------------------- helpers

    private static async Task ReachTenantAsync(SetupAssistantHarness harness)
    {
        await harness.RedeemTokenAsync();
        await harness.PostStepAsync("/welcome");
        await harness.PostStepAsync("/preflight");
        await harness.PostStepAsync("/preflight", ("action", "continue"));
        await harness.PostStepAsync("/mode", ("mode", "staging-no-send"));
    }

    private static async Task ReachConfirmationAsync(SetupAssistantHarness harness)
    {
        await ReachTenantAsync(harness);
        await harness.PostStepAsync(
            "/tenant",
            ("tenant_name", "example-tenant"),
            ("source_service", "example-service"),
            ("sender_email", "no-reply@example.test"),
            ("sender_display_name", "Example Notifications"));
        await harness.PostStepAsync(
            "/provider",
            ("service_token", ServiceToken),
            ("service_token_confirm", ServiceToken));
        await harness.PostStepAsync(
            "/acs",
            ("acs_connection_string", AcsConnectionString),
            ("acs_connection_string_confirm", AcsConnectionString),
            ("platform_sender_display_name", "Example Platform"));
    }
}
