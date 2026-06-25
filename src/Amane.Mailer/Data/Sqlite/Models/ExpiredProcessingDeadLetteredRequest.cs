namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record ExpiredProcessingDeadLetteredRequest(
    Guid Id,
    Guid MailRequestId,
    int AttemptNumber,
    string ErrorCode,
    string ErrorMessage);
