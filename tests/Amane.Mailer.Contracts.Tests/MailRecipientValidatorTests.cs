using Amane.Mailer.Contracts.MailRequests;

namespace Amane.Mailer.Contracts.Tests;

public sealed class MailRecipientValidatorTests
{
    private static MailRecipientDto Recipient(string email, string? displayName = null) =>
        new() { Email = email, DisplayName = displayName };

    private static MailRecipientDto[] Recipients(int count, string prefix = "recipient") =>
        Enumerable.Range(1, count).Select(i => Recipient($"{prefix}{i}@example.com")).ToArray();

    [Fact]
    public void TryValidate_rejects_zero_recipients_across_all_roles()
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: null,
            cc: null,
            bcc: null,
            out var result,
            out var failure);

        Assert.False(succeeded);
        Assert.Null(result);
        Assert.Equal(MailRecipientValidationFailure.InvalidRequest, failure);
    }

    [Fact]
    public void TryValidate_rejects_zero_recipients_with_empty_arrays()
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: [],
            cc: [],
            bcc: [],
            out var result,
            out var failure);

        Assert.False(succeeded);
        Assert.Equal(MailRecipientValidationFailure.InvalidRequest, failure);
    }

    [Theory]
    [InlineData(10, 0, 0)]
    [InlineData(0, 10, 0)]
    [InlineData(0, 0, 10)]
    public void TryValidate_accepts_exactly_ten_recipients_in_a_single_role(int toCount, int ccCount, int bccCount)
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: toCount > 0 ? Recipients(toCount, "to") : null,
            cc: ccCount > 0 ? Recipients(ccCount, "cc") : null,
            bcc: bccCount > 0 ? Recipients(bccCount, "bcc") : null,
            out var result,
            out _);

        Assert.True(succeeded);
        Assert.Equal(toCount + ccCount + bccCount, result!.TotalCount);
    }

    [Theory]
    [InlineData(11, 0, 0)]
    [InlineData(0, 11, 0)]
    [InlineData(0, 0, 11)]
    public void TryValidate_rejects_eleven_recipients_in_a_single_role_as_too_many(
        int toCount, int ccCount, int bccCount)
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: toCount > 0 ? Recipients(toCount, "to") : null,
            cc: ccCount > 0 ? Recipients(ccCount, "cc") : null,
            bcc: bccCount > 0 ? Recipients(bccCount, "bcc") : null,
            out _,
            out var failure);

        Assert.False(succeeded);
        Assert.Equal(MailRecipientValidationFailure.TooManyRecipients, failure);
    }

    [Fact]
    public void TryValidate_accepts_combined_total_of_twenty_across_roles()
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: Recipients(7, "to"),
            cc: Recipients(7, "cc"),
            bcc: Recipients(6, "bcc"),
            out var result,
            out _);

        Assert.True(succeeded);
        Assert.Equal(20, result!.TotalCount);
    }

    [Fact]
    public void TryValidate_rejects_combined_total_of_twenty_one_across_roles_as_too_many()
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: Recipients(7, "to"),
            cc: Recipients(7, "cc"),
            bcc: Recipients(7, "bcc"),
            out _,
            out var failure);

        Assert.False(succeeded);
        Assert.Equal(MailRecipientValidationFailure.TooManyRecipients, failure);
    }

    [Fact]
    public void TryValidate_accepts_cc_only()
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: null,
            cc: [Recipient("cc@example.com")],
            bcc: null,
            out var result,
            out _);

        Assert.True(succeeded);
        Assert.Empty(result!.To);
        Assert.Single(result.Cc);
        Assert.Empty(result.Bcc);
        Assert.False(result.IsLegacySingleTo);
    }

    [Fact]
    public void TryValidate_accepts_bcc_only()
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: null,
            cc: null,
            bcc: [Recipient("bcc@example.com")],
            out var result,
            out _);

        Assert.True(succeeded);
        Assert.False(result!.IsLegacySingleTo);
    }

    [Fact]
    public void TryValidate_single_to_is_legacy_shape()
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: [Recipient("to@example.com")],
            cc: null,
            bcc: null,
            out var result,
            out _);

        Assert.True(succeeded);
        Assert.True(result!.IsLegacySingleTo);
    }

    [Fact]
    public void TryValidate_rejects_duplicate_address_within_the_same_role()
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: [Recipient("dup@example.com"), Recipient("dup@example.com")],
            cc: null,
            bcc: null,
            out _,
            out var failure);

        Assert.False(succeeded);
        Assert.Equal(MailRecipientValidationFailure.InvalidRequest, failure);
    }

    [Fact]
    public void TryValidate_rejects_duplicate_address_across_roles()
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: [Recipient("dup@example.com")],
            cc: [Recipient("dup@example.com")],
            bcc: null,
            out _,
            out var failure);

        Assert.False(succeeded);
        Assert.Equal(MailRecipientValidationFailure.InvalidRequest, failure);
    }

    [Fact]
    public void TryValidate_rejects_duplicate_after_trim_and_case_folding()
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: [Recipient(" User@Example.com "), Recipient("user@example.com")],
            cc: null,
            bcc: null,
            out _,
            out var failure);

        Assert.False(succeeded);
        Assert.Equal(MailRecipientValidationFailure.InvalidRequest, failure);
    }

    [Fact]
    public void TryValidate_rejects_duplicate_even_when_display_names_differ()
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: [Recipient("dup@example.com", "Alice"), Recipient("dup@example.com", "Bob")],
            cc: null,
            bcc: null,
            out _,
            out var failure);

        Assert.False(succeeded);
        Assert.Equal(MailRecipientValidationFailure.InvalidRequest, failure);
    }

    [Fact]
    public void TryValidate_trims_address_but_preserves_case_for_the_canonical_value()
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: [Recipient("  User@Example.com  ")],
            cc: null,
            bcc: null,
            out var result,
            out _);

        Assert.True(succeeded);
        Assert.Equal("User@Example.com", result!.To[0].Address);
        Assert.Equal("user@example.com", result.To[0].AddressKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    [InlineData("missing-domain@")]
    [InlineData("@missing-local-part.com")]
    [InlineData("two@at@signs.com")]
    [InlineData("has space@example.com")]
    [InlineData("has<angle>@example.com")]
    [InlineData("Display Name <user@example.com>")]
    public void TryValidate_rejects_invalid_address_forms(string email)
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: [Recipient(email)],
            cc: null,
            bcc: null,
            out _,
            out var failure);

        Assert.False(succeeded);
        Assert.Equal(MailRecipientValidationFailure.InvalidRequest, failure);
    }

    [Fact]
    public void TryValidate_rejects_unicode_local_part()
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: [Recipient("田中@example.com")],
            cc: null,
            bcc: null,
            out _,
            out var failure);

        Assert.False(succeeded);
        Assert.Equal(MailRecipientValidationFailure.InvalidRequest, failure);
    }

    [Fact]
    public void TryValidate_rejects_idn_punycode_domain()
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: [Recipient("user@xn--fsq.example.com")],
            cc: null,
            bcc: null,
            out _,
            out var failure);

        Assert.False(succeeded);
        Assert.Equal(MailRecipientValidationFailure.InvalidRequest, failure);
    }

    [Theory]
    [InlineData("user@example.com\r")]
    [InlineData("user@example.com\n")]
    [InlineData("us\0er@example.com")]
    [InlineData("us\ter@example.com")]
    public void TryValidate_rejects_control_characters_in_address(string email)
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: [Recipient(email)],
            cc: null,
            bcc: null,
            out _,
            out var failure);

        Assert.False(succeeded);
        Assert.Equal(MailRecipientValidationFailure.InvalidRequest, failure);
    }

    [Fact]
    public void TryValidate_rejects_local_part_over_64_octets()
    {
        var longLocalPart = new string('a', 65);
        var succeeded = MailRecipientValidator.TryValidate(
            to: [Recipient($"{longLocalPart}@example.com")],
            cc: null,
            bcc: null,
            out _,
            out var failure);

        Assert.False(succeeded);
        Assert.Equal(MailRecipientValidationFailure.InvalidRequest, failure);
    }

    [Fact]
    public void TryValidate_accepts_local_part_at_64_octets()
    {
        var maxLocalPart = new string('a', 64);
        var succeeded = MailRecipientValidator.TryValidate(
            to: [Recipient($"{maxLocalPart}@example.com")],
            cc: null,
            bcc: null,
            out _,
            out _);

        Assert.True(succeeded);
    }

    [Fact]
    public void TryValidate_allows_japanese_display_name()
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: [Recipient("user@example.com", "山田太郎")],
            cc: null,
            bcc: null,
            out var result,
            out _);

        Assert.True(succeeded);
        Assert.Equal("山田太郎", result!.To[0].DisplayName);
    }

    [Fact]
    public void TryValidate_normalizes_whitespace_only_display_name_to_absent()
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: [Recipient("user@example.com", "   ")],
            cc: null,
            bcc: null,
            out var result,
            out _);

        Assert.True(succeeded);
        Assert.Null(result!.To[0].DisplayName);
    }

    [Theory]
    [InlineData("Name\r\nInjected")]
    [InlineData("Name\0Injected")]
    [InlineData("Name\u0085Injected")] // C1 control (NEL) embedded in otherwise-valid text
    [InlineData("\u0085")] // C1 control (NEL) alone -- must reject, not normalize to absent
    public void TryValidate_rejects_control_characters_in_display_name(string displayName)
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: [Recipient("user@example.com", displayName)],
            cc: null,
            bcc: null,
            out _,
            out var failure);

        Assert.False(succeeded);
        Assert.Equal(MailRecipientValidationFailure.InvalidRequest, failure);
    }

    [Fact]
    public void TryValidate_preserves_ordinal_and_role_array_order()
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: [Recipient("first@example.com"), Recipient("second@example.com")],
            cc: null,
            bcc: null,
            out var result,
            out _);

        Assert.True(succeeded);
        Assert.Equal(0, result!.To[0].Ordinal);
        Assert.Equal(1, result.To[1].Ordinal);
        Assert.Equal(MailRecipientRole.To, result.To[0].Role);
        Assert.Equal("first@example.com", result.To[0].Address);
        Assert.Equal("second@example.com", result.To[1].Address);
    }

    [Fact]
    public void TryValidate_rejects_a_null_recipient_entry_in_the_array()
    {
        var succeeded = MailRecipientValidator.TryValidate(
            to: [Recipient("user@example.com"), null!],
            cc: null,
            bcc: null,
            out _,
            out var failure);

        Assert.False(succeeded);
        Assert.Equal(MailRecipientValidationFailure.InvalidRequest, failure);
    }
}
