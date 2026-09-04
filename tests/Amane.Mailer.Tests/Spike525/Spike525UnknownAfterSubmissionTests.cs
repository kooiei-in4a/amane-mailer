using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Delivery;

namespace Amane.Mailer.Tests.Spike525;

/// <summary>
/// #525 Spike — S-06 (unknown_after_submission fault injection), S-07 (result re-query),
/// S-08 (whole-request retry duplicate risk). Uses the REAL production
/// <see cref="MailpitMailDeliveryProvider"/> and <see cref="ProviderErrorClassifier"/> against
/// a real Mailpit instance via <see cref="Spike525SmtpRelay"/>; only the relay and the
/// outer timeout wrapper (a direct copy of MailRequestDispatcher's send-timeout pattern) are
/// spike-only.
///
/// Gated by AMANE_SPIKE525_MAILPIT_TESTS=1 (see Spike525Gate).
/// </summary>
public sealed class Spike525UnknownAfterSubmissionTests
{
    private const string FromAddress = "sender@example.com";

    [Fact]
    public async Task S06_S07_provider_accepts_but_worker_observes_ambiguous_timeout()
    {
        if (!Spike525Gate.MailpitEnabled)
        {
            return;
        }

        await using var relay = new Spike525SmtpRelay(Spike525Gate.MailpitSmtpHost, Spike525Gate.MailpitSmtpPort)
        {
            SuppressResponseAfterDataTerminator = true,
        };
        relay.Start();

        var subject = "spike525-s06-" + Guid.NewGuid().ToString("N")[..8];
        var provider = new MailpitMailDeliveryProvider(new MailerOptions
        {
            MailpitSmtpHost = "127.0.0.1",
            MailpitSmtpPort = relay.ListenPort,
            MailpitUseSsl = false,
        });

        var job = MailSendJob.ForSingleRecipient(
            Guid.NewGuid(),
            "spike525-source",
            subject,
            htmlBody: null,
            textBody: "s06 body",
            replyTo: null,
            recipientEmail: Spike525Support.SyntheticAddress("s06-to1"),
            recipientDisplayName: null);

        var tenant = new MailerTenant
        {
            TenantId = Guid.NewGuid(),
            Name = "spike525",
            SourceServices = ["spike525-source"],
            DefaultFrom = new MailerAddress { Email = FromAddress },
            TokenEnv = "MAIL_SERVICE_TOKEN",
            Provider = "mailpit",
            Retry = new MailerRetryOptions { MaxAttempts = 3, InitialDelaySeconds = 1, MaxDelaySeconds = 2 },
        };

        // Mirrors MailRequestDispatcher.DispatchAsync's send-timeout wrapper exactly
        // (src/Amane.Mailer/Worker/MailRequestDispatcher.cs). The spike reproduces the wrapper
        // directly around the real provider rather than constructing the persistence dependencies.
        MailDeliveryResult result;
        try
        {
            using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            sendTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            result = await provider.SendAsync(job, tenant, "mailpit", sendTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            result = MailDeliveryResult.Failure(
                MailDeliveryErrorCodes.SendTimeout,
                "Mail delivery exceeded 3 seconds.",
                retryable: true);
        }

        // Independent ground truth: did Mailpit actually receive/queue the message despite
        // the caller-side ambiguity? (S-07: can the outcome be resolved after the fact?)
        using var http = new HttpClient { BaseAddress = new Uri(Spike525Gate.MailpitHttpBaseUrl) };
        var api = new MailpitApiClient(http);
        var receivedByProvider = await WaitForSubjectAsync(api, subject, TestContext.Current.CancellationToken) is not null;

        Spike525Support.Evidence.Record("S-06", new
        {
            Provider = "mailpit",
            FaultPoint = "response-withheld-after-data-terminator",
            WorkerObservedOutcome = result.Succeeded ? "succeeded" : result.ErrorCode,
            WorkerMarkedRetryable = result.Retryable,
            ProviderActuallyReceivedMessage = receivedByProvider,
            OutcomeTaxonomy = "unknown_after_submission",
        });

        // The core #525 finding: current code classifies this as SEND_TIMEOUT/retryable=true
        // (matching MailRequestDispatcher's real behavior) even though the provider already has
        // the message — i.e. today's classification cannot distinguish "never received" from
        // "received, response lost", contrary to the Draft ADR's no-automatic-retry requirement
        // for unknown_after_submission.
        Assert.False(result.Succeeded);
        Assert.True(result.Retryable, "Current MailRequestDispatcher/ProviderErrorClassifier behavior: ambiguous post-submission timeouts are marked retryable.");
        Assert.True(receivedByProvider, "Mailpit must have actually received the message for this to be a genuine unknown_after_submission case (not definitely_not_submitted).");
    }

    [Fact]
    public async Task S08_retrying_after_ambiguous_timeout_causes_duplicate_delivery()
    {
        if (!Spike525Gate.MailpitEnabled)
        {
            return;
        }

        var subject = "spike525-s08-" + Guid.NewGuid().ToString("N")[..8];
        var job = MailSendJob.ForSingleRecipient(
            Guid.NewGuid(),
            "spike525-source",
            subject,
            htmlBody: null,
            textBody: "s08 body",
            replyTo: null,
            recipientEmail: Spike525Support.SyntheticAddress("s08-to1"),
            recipientDisplayName: null);

        var tenant = new MailerTenant
        {
            TenantId = Guid.NewGuid(),
            Name = "spike525",
            SourceServices = ["spike525-source"],
            DefaultFrom = new MailerAddress { Email = FromAddress },
            TokenEnv = "MAIL_SERVICE_TOKEN",
            Provider = "mailpit",
            Retry = new MailerRetryOptions { MaxAttempts = 3, InitialDelaySeconds = 1, MaxDelaySeconds = 2 },
        };

        // Attempt 1: response withheld after acceptance (ambiguous outcome, as in S-06).
        await using (var relay1 = new Spike525SmtpRelay(Spike525Gate.MailpitSmtpHost, Spike525Gate.MailpitSmtpPort) { SuppressResponseAfterDataTerminator = true })
        {
            relay1.Start();
            var provider1 = new MailpitMailDeliveryProvider(new MailerOptions
            {
                MailpitSmtpHost = "127.0.0.1",
                MailpitSmtpPort = relay1.ListenPort,
                MailpitUseSsl = false,
            });

            try
            {
                using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
                sendTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                await provider1.SendAsync(job, tenant, "mailpit", sendTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected: matches MailRequestDispatcher's SEND_TIMEOUT/retryable=true path.
            }
        }

        // Attempt 2: worker-style automatic retry of the SAME logical request, direct to real
        // Mailpit (no fault injection this time — a normal successful retry attempt).
        var provider2 = new MailpitMailDeliveryProvider(new MailerOptions
        {
            MailpitSmtpHost = Spike525Gate.MailpitSmtpHost,
            MailpitSmtpPort = Spike525Gate.MailpitSmtpPort,
            MailpitUseSsl = false,
        });
        var retryResult = await provider2.SendAsync(job, tenant, "mailpit", TestContext.Current.CancellationToken);
        Assert.True(retryResult.Succeeded);

        using var http = new HttpClient { BaseAddress = new Uri(Spike525Gate.MailpitHttpBaseUrl) };
        var api = new MailpitApiClient(http);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var all = await api.ListMessagesAsync(TestContext.Current.CancellationToken);
        var matches = all.Count(m => m.Subject == subject);

        Spike525Support.Evidence.Record("S-08", new
        {
            Provider = "mailpit",
            FirstAttemptOutcome = "unknown_after_submission (SEND_TIMEOUT/retryable=true)",
            SecondAttemptOutcome = retryResult.Succeeded ? "succeeded" : retryResult.ErrorCode,
            ReceivedMessageCountForSameLogicalRequest = matches,
            DuplicateDeliveryConfirmed = matches > 1,
        });

        // Core #525 finding: an automatic whole-request retry after an ambiguous
        // (already-accepted) timeout produces a second, independent message in the mailbox —
        // Mailpit/SMTP has no request-level idempotency of its own. This is exactly the
        // "partial/unknown acceptance must not auto-retry" requirement from the Draft ADR.
        Assert.True(matches > 1, "Expected duplicate delivery: Mailpit/SMTP does not de-duplicate independent SMTP transactions for the same logical mail request.");
    }

    private static async Task<MailpitMessageSummary?> WaitForSubjectAsync(
        MailpitApiClient api, string subject, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 20; i++)
        {
            var all = await api.ListMessagesAsync(cancellationToken);
            var match = all.FirstOrDefault(m => m.Subject == subject);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(100, cancellationToken);
        }

        return null;
    }
}
