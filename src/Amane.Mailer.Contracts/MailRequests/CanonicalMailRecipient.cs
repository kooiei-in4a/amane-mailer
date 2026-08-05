namespace Amane.Mailer.Contracts.MailRequests;

/// <summary>
/// A single validated recipient (ADR 0023 D-01). <see cref="Address"/> is the trimmed,
/// case-preserved address used for delivery and for payload hashing (ADR 0023 D-02).
/// <see cref="AddressKey"/> (trim + lowercase-invariant) is used only for duplicate detection
/// and never appears in the payload hash.
/// </summary>
public sealed record CanonicalMailRecipient
{
    public required MailRecipientRole Role { get; init; }

    /// <summary>Zero-based position within its role's original submission order.</summary>
    public required int Ordinal { get; init; }

    public required string Address { get; init; }

    public required string AddressKey { get; init; }

    /// <summary>Null when unspecified or whitespace-only in the source request.</summary>
    public string? DisplayName { get; init; }
}
