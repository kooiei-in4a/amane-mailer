namespace Amane.Mailer.Contracts.MailRequests;

/// <summary>
/// Validated recipient set for a mail request (ADR 0023 D-01). Construct via
/// <see cref="MailRecipientValidator.Validate"/> — this type carries only already-validated data.
/// </summary>
public sealed record CanonicalMailRecipientSet
{
    public required IReadOnlyList<CanonicalMailRecipient> To { get; init; }

    public required IReadOnlyList<CanonicalMailRecipient> Cc { get; init; }

    public required IReadOnlyList<CanonicalMailRecipient> Bcc { get; init; }

    public required IReadOnlyList<CanonicalMailRecipient> All { get; init; }

    public int TotalCount => All.Count;

    /// <summary>
    /// True when the request is exactly one To recipient and no Cc/Bcc — the only shape the
    /// runtime currently persists and delivers (ADR 0023 non-goals: persistence/migration is a
    /// separate, not-yet-implemented follow-up).
    /// </summary>
    public bool IsLegacySingleTo => To.Count == 1 && Cc.Count == 0 && Bcc.Count == 0;
}
