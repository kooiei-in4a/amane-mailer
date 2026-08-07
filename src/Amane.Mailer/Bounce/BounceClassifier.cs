using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Bounce;

/// <summary>
/// Bounce and provider-suppression classification (ADR 0020 D-06).
/// </summary>
public static class BounceClassifier
{
    public const string HardBounceStatus = "Bounced";
    public const string SuppressedStatus = "Suppressed";
    public const string DeliveredStatus = "Delivered";

    public static bool IsHardBounce(string? deliveryStatus) =>
        string.Equals(deliveryStatus, HardBounceStatus, StringComparison.Ordinal);

    public static bool IsSuppressed(string? deliveryStatus) =>
        string.Equals(deliveryStatus, SuppressedStatus, StringComparison.Ordinal);

    /// <summary>
    /// Returns whether the provider status must be added to the tenant suppression list.
    /// </summary>
    public static bool ShouldSuppress(string? deliveryStatus) =>
        IsHardBounce(deliveryStatus) || IsSuppressed(deliveryStatus);

    public static bool IsDelivered(string? deliveryStatus) =>
        string.Equals(deliveryStatus, DeliveredStatus, StringComparison.Ordinal);

    /// <summary>
    /// Every non-empty provider status is retained as recipient delivery-event history.
    /// </summary>
    public static bool ShouldRecordBounceEvent(string? deliveryStatus) =>
        !string.IsNullOrWhiteSpace(deliveryStatus);

    public static MailRecipientDeliveryState? AppliedRecipientState(string? deliveryStatus)
    {
        if (IsDelivered(deliveryStatus))
        {
            return MailRecipientDeliveryState.Delivered;
        }

        if (IsHardBounce(deliveryStatus))
        {
            return MailRecipientDeliveryState.Bounced;
        }

        if (IsSuppressed(deliveryStatus))
        {
            return MailRecipientDeliveryState.Suppressed;
        }

        return null;
    }
}
