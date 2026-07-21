namespace Amane.Mailer.Webhooks;

public enum DeliveryEventState
{
    Pending = 0,
    Delivering = 1,
    Delivered = 2,
    DeadLettered = 3,
}

public enum DeliveryEventFinalizeOutcome
{
    Delivered,
    RetryScheduled,
    DeadLettered,
}
