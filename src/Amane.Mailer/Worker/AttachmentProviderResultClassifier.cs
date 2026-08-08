using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Delivery;

namespace Amane.Mailer.Worker;

/// <summary>Tri-state provider result classification for attachment requests (ADR 0022 D-08).</summary>
public enum AttachmentProviderOutcome
{
    Succeeded,
    DefinitiveFailed,
    Ambiguous,
}

/// <summary>
/// Classifies a provider result as Succeeded, DefinitiveFailed (provider non-acceptance is
/// proven), or Ambiguous (acceptance cannot be disproven). Deliberately conservative: every
/// case not provably one of the first two defaults to Ambiguous, since converging to
/// DeliveryUnknown is always safe while wrongly asserting DefinitiveFailed or Succeeded is not
/// (ADR 0022 D-08: "未送信回復より二重送信防止を優先").
/// </summary>
public static class AttachmentProviderResultClassifier
{
    public static AttachmentProviderOutcome Classify(MailDeliveryResult result)
    {
        if (result.Succeeded)
        {
            return AttachmentProviderOutcome.Succeeded;
        }

        return result.ErrorCode switch
        {
            // ACS LRO completed with a well-formed, non-Succeeded terminal status: the provider
            // told us conclusively.
            MailDeliveryErrorCodes.AcsSendFailed => AttachmentProviderOutcome.DefinitiveFailed,

            // A non-retryable RequestFailedException is everything except 408/429/5xx (see
            // AcsMailDeliveryProvider) -- i.e. the synchronous "start send" call was rejected
            // (4xx other than 408/429) before ACS ever queued the message.
            MailDeliveryErrorCodes.AcsRequestFailed when !result.Retryable =>
                AttachmentProviderOutcome.DefinitiveFailed,

            // TLS/SASL failure: no message data was ever transmitted.
            MailDeliveryErrorCodes.ProviderAuth => AttachmentProviderOutcome.DefinitiveFailed,

            _ => AttachmentProviderOutcome.Ambiguous,
        };
    }
}
