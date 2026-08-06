namespace Amane.Mailer.Data.Sqlite.Models;

/// <summary>
/// Durable request-unique provider submission evidence for a non-attachment request
/// (ADR 0023 D-04, Issue #546). Absence of a row (<c>NoEvidence</c>) is not represented here --
/// callers treat a <see langword="null"/> read as "no row exists" per the ADR 0023 D-06 seven
/// conditions, not as a stored enum value.
/// </summary>
public sealed record MailPlainSubmissionRow(
    Guid RequestId,
    MailPlainSubmissionEvidenceState EvidenceState,
    MailPlainSubmissionEvidenceOrigin EvidenceOrigin,
    string? Provider,
    Guid? ClaimToken,
    DateTimeOffset? StartedAt,
    string? ProviderMessageId,
    DateTimeOffset? ResolvedAt);
