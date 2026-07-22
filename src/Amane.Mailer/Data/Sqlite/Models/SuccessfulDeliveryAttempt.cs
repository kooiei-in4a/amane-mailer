namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record SuccessfulDeliveryAttempt(
    int AttemptNumber,
    string Provider,
    string? ProviderMessageId);
