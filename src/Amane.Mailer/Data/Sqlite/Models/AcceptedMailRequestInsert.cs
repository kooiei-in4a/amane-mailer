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
}
