using Amane.Mailer.Contracts.MailRequests;

namespace Amane.Mailer.Api;

/// <summary>
/// Shared scheduled_at validation for create and reschedule use-cases.
/// </summary>
public static class MailRequestScheduleValidator
{
    public static IResult? ValidateScheduledAt(DateTimeOffset? scheduledAt, DateTimeOffset now)
    {
        if (scheduledAt is null)
        {
            return null;
        }

        var scheduledAtUtc = scheduledAt.Value.ToUniversalTime();
        if (scheduledAtUtc < now)
        {
            return MailRequestHttpErrorMapper.Error(
                StatusCodes.Status422UnprocessableEntity,
                MailerErrorCodes.ScheduledAtInPast);
        }

        if (scheduledAtUtc > now.Add(MailRequestScheduleLimits.MaxScheduledAhead))
        {
            return MailRequestHttpErrorMapper.Error(
                StatusCodes.Status422UnprocessableEntity,
                MailerErrorCodes.ScheduledAtTooFar);
        }

        return null;
    }
}
