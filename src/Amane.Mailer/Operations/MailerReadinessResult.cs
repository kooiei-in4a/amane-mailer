namespace Amane.Mailer.Operations;

/// <summary>
/// Internal readiness outcome. <see cref="FailureReason"/> is null when ready.
/// </summary>
public readonly record struct MailerReadinessResult(bool IsReady, string? FailureReason)
{
    public static MailerReadinessResult Ready() => new(true, null);

    public static MailerReadinessResult NotReady(string failureReason) =>
        new(false, failureReason);
}
