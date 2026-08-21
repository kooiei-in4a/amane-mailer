using Amane.Mailer.Attachments.Validation;
using Amane.Mailer.Contracts.MailRequests;

namespace Amane.Mailer.Data.Sqlite.Models;

public sealed class AcceptedMailRequestInsert
{
    public required Guid Id { get; init; }

    public required Guid TenantId { get; init; }

    public required string SourceService { get; init; }

    public required Guid MailRequestId { get; init; }

    public required string Purpose { get; init; }

    public required string PayloadJson { get; init; }

    public required string PayloadHash { get; init; }

    public required string Subject { get; init; }

    public string? HtmlBody { get; init; }

    public string? TextBody { get; init; }

    public string? ReplyTo { get; init; }

    public required string RecipientEmail { get; init; }

    public string? RecipientDisplayName { get; init; }

    public string? MetadataJson { get; init; }

    public required int MaxAttempts { get; init; }

    public required DateTimeOffset AcceptedAt { get; init; }

    public DateTimeOffset? ScheduledAt { get; init; }

    /// <summary>
    /// Canonical recipients to persist with the accepted request. When null, the legacy
    /// single-To fields are used to derive one To recipient so existing direct callers remain
    /// source-compatible while the handler passes its validated canonical set explicitly.
    /// </summary>
    public IReadOnlyList<CanonicalMailRecipient>? Recipients { get; init; }

    /// <summary>
    /// Canonical attachment metadata, already staged to the spool's request-scoped staging
    /// directory. Null/empty means no attachments. <see cref="MailRequestAcceptStore.InsertAcceptedAsync"/>
    /// commits the staging directory to the committed spool root before opening the SQLite
    /// transaction (ADR 0022 D-08 steps 4-5), then inserts one <c>mail_request_attachments</c>
    /// row per entry in the same transaction as the <c>mail_requests</c> row.
    /// </summary>
    public IReadOnlyList<CanonicalAttachmentMetadata>? Attachments { get; init; }
}
