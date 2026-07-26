namespace Amane.Mailer.Bounce;

/// <summary>
/// Hard-bounce classification (ADR 0020 D-06). Only observed <c>Bounced</c> suppresses.
/// </summary>
public static class BounceClassifier
{
    public const string HardBounceStatus = "Bounced";
    public const string DeliveredStatus = "Delivered";

    public static bool IsHardBounce(string? deliveryStatus) =>
        string.Equals(deliveryStatus, HardBounceStatus, StringComparison.Ordinal);

    public static bool IsDelivered(string? deliveryStatus) =>
        string.Equals(deliveryStatus, DeliveredStatus, StringComparison.Ordinal);

    /// <summary>
    /// Non-Delivered delivery-report statuses are recorded; only <see cref="HardBounceStatus"/> suppresses.
    /// </summary>
    public static bool ShouldRecordBounceEvent(string? deliveryStatus) =>
        !string.IsNullOrWhiteSpace(deliveryStatus) && !IsDelivered(deliveryStatus);
}
