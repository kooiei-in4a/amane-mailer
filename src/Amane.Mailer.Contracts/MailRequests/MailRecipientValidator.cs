using System.Net.Mail;

namespace Amane.Mailer.Contracts.MailRequests;

/// <summary>
/// Closed failure categories for <see cref="MailRecipientValidator"/> (ADR 0023 D-01). No new
/// reason codes are introduced by recipient validation; callers map these to the existing
/// <see cref="MailerErrorCodes.TooManyRecipients"/> / <see cref="MailerErrorCodes.InvalidRequest"/>.
/// Never carries raw recipient values.
/// </summary>
public enum MailRecipientValidationFailure
{
    TooManyRecipients,
    InvalidRequest,
}

/// <summary>
/// Validates and canonicalizes To/Cc/Bcc recipients (ADR 0023 D-01). Used by both the runtime
/// handler and (indirectly, via <see cref="CanonicalMailRecipient.Address"/>) the payload hasher,
/// so validation and hashing agree on what a "recipient" is.
/// </summary>
public static class MailRecipientValidator
{
    public const int MaxRecipientsPerRole = 10;
    public const int MaxRecipientsTotal = 20;
    public const int MaxLocalPartLength = 64;
    public const int MaxAddressLength = 254;

    public static bool TryValidate(
        IReadOnlyList<MailRecipientDto>? to,
        IReadOnlyList<MailRecipientDto>? cc,
        IReadOnlyList<MailRecipientDto>? bcc,
        out CanonicalMailRecipientSet? result,
        out MailRecipientValidationFailure failure)
    {
        result = null;

        var toCount = to?.Count ?? 0;
        var ccCount = cc?.Count ?? 0;
        var bccCount = bcc?.Count ?? 0;
        var total = toCount + ccCount + bccCount;

        if (total == 0)
        {
            failure = MailRecipientValidationFailure.InvalidRequest;
            return false;
        }

        if (toCount > MaxRecipientsPerRole
            || ccCount > MaxRecipientsPerRole
            || bccCount > MaxRecipientsPerRole
            || total > MaxRecipientsTotal)
        {
            failure = MailRecipientValidationFailure.TooManyRecipients;
            return false;
        }

        var seenAddressKeys = new HashSet<string>(StringComparer.Ordinal);
        var allEntries = new List<CanonicalMailRecipient>(total);

        if (!TryCanonicalizeRole(to, MailRecipientRole.To, seenAddressKeys, allEntries, out var canonicalTo)
            || !TryCanonicalizeRole(cc, MailRecipientRole.Cc, seenAddressKeys, allEntries, out var canonicalCc)
            || !TryCanonicalizeRole(bcc, MailRecipientRole.Bcc, seenAddressKeys, allEntries, out var canonicalBcc))
        {
            failure = MailRecipientValidationFailure.InvalidRequest;
            return false;
        }

        result = new CanonicalMailRecipientSet
        {
            To = canonicalTo,
            Cc = canonicalCc,
            Bcc = canonicalBcc,
            All = allEntries,
        };
        failure = default;
        return true;
    }

    private static bool TryCanonicalizeRole(
        IReadOnlyList<MailRecipientDto>? role,
        MailRecipientRole roleKind,
        HashSet<string> seenAddressKeys,
        List<CanonicalMailRecipient> allEntries,
        out List<CanonicalMailRecipient> canonicalRole)
    {
        canonicalRole = new List<CanonicalMailRecipient>(role?.Count ?? 0);
        if (role is null)
        {
            return true;
        }

        for (var ordinal = 0; ordinal < role.Count; ordinal++)
        {
            var entry = role[ordinal];
            if (entry is null
                || !TryValidateAddress(entry.Email, out var address, out var addressKey)
                || !TryValidateDisplayName(entry.DisplayName, out var displayName))
            {
                return false;
            }

            if (!seenAddressKeys.Add(addressKey))
            {
                // Duplicate address_key, within this role or across To/Cc/Bcc (ADR 0023 D-01).
                return false;
            }

            var canonical = new CanonicalMailRecipient
            {
                Role = roleKind,
                Ordinal = ordinal,
                Address = address,
                AddressKey = addressKey,
                DisplayName = displayName,
            };
            canonicalRole.Add(canonical);
            allEntries.Add(canonical);
        }

        return true;
    }

    /// <summary>
    /// ASCII-only address validation (ADR 0023 D-01, v1.3.0 scope): rejects non-ASCII, control
    /// characters (including CR/LF/NUL), embedded whitespace/angle brackets (which would indicate
    /// a display name folded into the address), IDN/Punycode domain labels ("xn--"), and enforces
    /// the 64-octet local-part / 254-character overall length limits. <paramref name="address"/>
    /// is the trimmed, case-preserved value used both for delivery and for payload hashing.
    /// <paramref name="addressKey"/> (trim + lowercase-invariant, matching
    /// RecipientEmailNormalizer) is for duplicate detection only and must never be used for
    /// hashing or delivery.
    /// </summary>
    private static bool TryValidateAddress(string rawEmail, out string address, out string addressKey)
    {
        address = string.Empty;
        addressKey = string.Empty;

        if (string.IsNullOrEmpty(rawEmail))
        {
            return false;
        }

        // Checked on the raw value, before trimming: CR/LF/NUL/TAB/etc. must never be silently
        // stripped by Trim() just because they sit at the edges of the string (that would let a
        // trailing "\r\n" through as if it were ordinary whitespace).
        foreach (var character in rawEmail)
        {
            if (character > 0x7E || character < 0x20 || character == 0x7F)
            {
                // Non-ASCII (rejects IDN/Unicode local-part/domain) or a control character
                // (rejects CR/LF/NUL/TAB/etc., anywhere in the string).
                return false;
            }
        }

        var trimmed = rawEmail.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxAddressLength)
        {
            return false;
        }

        if (trimmed.Contains('<', StringComparison.Ordinal)
            || trimmed.Contains('>', StringComparison.Ordinal)
            || trimmed.Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        var atIndex = trimmed.IndexOf('@');
        if (atIndex <= 0 || atIndex != trimmed.LastIndexOf('@'))
        {
            return false;
        }

        var localPart = trimmed[..atIndex];
        var domain = trimmed[(atIndex + 1)..];
        if (localPart.Length == 0 || localPart.Length > MaxLocalPartLength || domain.Length == 0)
        {
            return false;
        }

        var labels = domain.Split('.');
        if (labels.Any(label => label.Length == 0)
            || labels.Any(label => label.StartsWith("xn--", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!MailAddress.TryCreate(trimmed, out var parsed)
            || !string.Equals(parsed.Address, trimmed, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(parsed.DisplayName))
        {
            // Guards against a display name folding into the address field via the MailAddress
            // parser (e.g. "Name <addr>" successfully parsing to just the address).
            return false;
        }

        address = trimmed;
        addressKey = trimmed.ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// Whitespace-only display names normalize to absent/null. Otherwise rejects control
    /// characters -- both C0 (U+0000-U+001F, U+007F; includes CR/LF/NUL) and C1
    /// (U+0080-U+009F; e.g. NEL U+0085) -- via <see cref="char.IsControl(char)"/>. Unicode
    /// (e.g. Japanese) display names are allowed. The control-character check runs before the
    /// whitespace-only check: some C1 controls (notably NEL, U+0085) are classified as
    /// whitespace by <see cref="string.IsNullOrWhiteSpace(string?)"/>, and a value made up
    /// entirely of such characters must be rejected as invalid input, not silently normalized
    /// to absent.
    /// </summary>
    private static bool TryValidateDisplayName(string? rawDisplayName, out string? displayName)
    {
        if (rawDisplayName is null)
        {
            displayName = null;
            return true;
        }

        foreach (var character in rawDisplayName)
        {
            if (char.IsControl(character))
            {
                displayName = null;
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(rawDisplayName))
        {
            displayName = null;
            return true;
        }

        displayName = rawDisplayName;
        return true;
    }
}
