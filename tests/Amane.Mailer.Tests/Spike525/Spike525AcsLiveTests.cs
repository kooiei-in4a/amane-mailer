using System.Text.Json;
using Azure;
using Azure.Communication.Email;
using Azure.Core.Pipeline;
using Amane.Mailer.Bounce;
using Amane.Mailer.Configuration;
using Amane.Mailer.Delivery;
using AcsEmailAddress = Azure.Communication.Email.EmailAddress;
using AcsEmailAttachment = Azure.Communication.Email.EmailAttachment;
using AcsEmailClient = Azure.Communication.Email.EmailClient;
using AcsEmailContent = Azure.Communication.Email.EmailContent;
using AcsEmailMessage = Azure.Communication.Email.EmailMessage;
using AcsEmailRecipients = Azure.Communication.Email.EmailRecipients;

namespace Amane.Mailer.Tests.Spike525;

/// <summary>
/// #525 Spike — gated LIVE Azure Communication Services fixtures (S-01, S-03, S-04, S-05, S-06,
/// S-07, S-08, S-09, S-10, S-14, S-15, S-16). Runs ONLY when
/// <see cref="Spike525AcsGate.AcsLiveEnabled"/> is true, which requires the operator to have
/// already run <c>Test-Spike525CredentialGate.ps1</c> to PASS and dot-sourced
/// <c>Import-Spike525Environment.ps1</c> into the current process (see AGENTS.md / Issue #525).
/// Every real send reserves a slot from <see cref="Spike525AcsSendBudget"/> first
/// (AMANE_ACS_SPIKE_MAX_SENDS), so a run can never exceed the configured live-send cap.
///
/// This is deliberately test-only, greenfield ACS message construction (multi-recipient
/// EmailRecipients, attachments) — the production <see cref="AcsMailDeliveryProvider"/> only
/// supports a single recipient today, so it cannot be reused for CC/BCC/attachment fixtures
/// without pre-committing a public multi-recipient contract this Spike must not decide.
/// Fault classification (S-06) DOES reuse the real production
/// <see cref="ProviderErrorClassifier"/> so the finding is directly comparable to today's actual
/// runtime behavior, exactly as the Mailpit S-06/S-07/S-08 fixtures do.
///
/// Evidence is value-free per #525 policy: no raw recipient/sender/BCC, no full operation or
/// message IDs (only <see cref="Spike525Support.ShortHash"/>), no raw Event Grid payload, no
/// connection strings/keys. Queue messages are received (temporarily invisible) but never
/// deleted by this Spike — a non-destructive, reversible read, since this queue is a disposable
/// non-production resource dedicated to this Spike and no other consumer is expected to be
/// running concurrently.
/// </summary>
[Collection(Spike525AcsLiveCollection.Name)]
public sealed class Spike525AcsLiveTests
{
    [Fact]
    public async Task S01_BccOnly_And_S15_S16_AttachmentSubmission()
    {
        if (!Spike525AcsGate.AcsLiveEnabled)
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var client = BuildClient();

        // --- S-01: BCC-only recipients (To empty) ---
        string bccOnlyOutcome;
        AcsEmailRecipients? bccOnlyRecipients = null;
        try
        {
            bccOnlyRecipients = new AcsEmailRecipients(
                to: [],
                cc: [],
                bcc: [new AcsEmailAddress(Spike525AcsGate.RecipientAddress)]);
            bccOnlyOutcome = "client_side_constructor_accepted_empty_to";
        }
        catch (Exception ex)
        {
            bccOnlyOutcome = "client_side_constructor_threw_" + ex.GetType().Name;
        }

        if (bccOnlyRecipients is not null)
        {
            Spike525AcsSendBudget.Reserve("S01-bcc-only");
            var bccContent = new AcsEmailContent("spike525-acs-s01-bcc-only")
            {
                PlainText = "spike525 ACS live fixture S-01 bcc-only.",
            };
            var bccMessage = new AcsEmailMessage(Spike525AcsGate.SenderAddress, bccOnlyRecipients, bccContent);
            try
            {
                var op = await client.SendAsync(WaitUntil.Completed, bccMessage, Guid.NewGuid(), ct);
                bccOnlyOutcome = "server_accepted_status_" + (op.HasValue ? op.Value.Status.ToString() : "unknown");
            }
            catch (RequestFailedException ex)
            {
                bccOnlyOutcome = "server_rejected_status_" + ex.Status;
            }
        }

        Spike525Support.Evidence.Record("S-01-bcc-only", new
        {
            Provider = "acs",
            Scenario = "to-empty-bcc-only",
            Outcome = bccOnlyOutcome,
            SendBudgetUsed = Spike525AcsSendBudget.Used,
            SendBudgetMax = Spike525AcsGate.MaxSends,
        });

        // --- S-15 / S-16: multiple attachments incl. NFC Japanese filename ---
        var attachRecipients = new AcsEmailRecipients(to: [new AcsEmailAddress(Spike525AcsGate.RecipientAddress)]);
        var attachContent = new AcsEmailContent("spike525-acs-s15-s16")
        {
            PlainText = "spike525 ACS live fixture S-15/S-16 attachments.",
        };
        var attachMessage = new AcsEmailMessage(Spike525AcsGate.SenderAddress, attachRecipients, attachContent);
        var japaneseFileName = "請求書.pdf".Normalize(System.Text.NormalizationForm.FormC);
        var attachmentNames = new[] { "a-first.pdf", "b-second.png", japaneseFileName };
        attachMessage.Attachments.Add(new AcsEmailAttachment("a-first.pdf", "application/pdf", BinaryData.FromBytes(SyntheticPdfBytes())));
        attachMessage.Attachments.Add(new AcsEmailAttachment("b-second.png", "image/png", BinaryData.FromBytes(SyntheticPngBytes())));
        attachMessage.Attachments.Add(new AcsEmailAttachment(japaneseFileName, "application/pdf", BinaryData.FromBytes(SyntheticPdfBytes())));

        Spike525AcsSendBudget.Reserve("S15-S16-attachments");
        var attachOperationId = Guid.NewGuid();
        var attachOp = await client.SendAsync(WaitUntil.Completed, attachMessage, attachOperationId, ct);

        Spike525Support.Evidence.Record("S-15-S16", new
        {
            Provider = "acs",
            AttachmentCount = attachMessage.Attachments.Count,
            AttachmentNamesAreNfc = attachmentNames.All(n => n.IsNormalized(System.Text.NormalizationForm.FormC)),
            SyncStatus = attachOp.HasValue ? attachOp.Value.Status.ToString() : "unknown",
            OperationIdShortHash = Spike525Support.ShortHash(attachOp.Id),
            TransportEncoding = "acs-rest-json-base64-inherent",
            Note = "ACS EmailAttachment.Content is BinaryData sent as base64 JSON on the wire; unlike MIME/SMTP there is no Content-Transfer-Encoding auto-selection, so the Mailpit S-15b LF/CRLF canonicalization gap structurally cannot occur here. Delivered-byte digest at the real mailbox was NOT independently verified in this environment (no inbox-read capability available to this Spike session) — residual gap for Agent B / manual follow-up.",
            SendBudgetUsed = Spike525AcsSendBudget.Used,
            SendBudgetMax = Spike525AcsGate.MaxSends,
        });

        Assert.True(bccOnlyRecipients is not null || bccOnlyOutcome.StartsWith("client_side_constructor_threw_", StringComparison.Ordinal));
        Assert.True(attachOp.HasValue, "S-15/S-16 attachment send did not reach a terminal ACS status.");
    }

    [Fact]
    public async Task S06_S07_FaultInjectionAndRequery()
    {
        if (!Spike525AcsGate.AcsLiveEnabled)
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var faultTransport = new Spike525AcsFaultAfterSubmissionTransport();
        var faultClient = BuildClient(faultTransport);

        var recipients = new AcsEmailRecipients(to: [new AcsEmailAddress(Spike525AcsGate.RecipientAddress)]);
        var content = new AcsEmailContent("spike525-acs-s06-s07")
        {
            PlainText = "spike525 ACS live fixture S-06/S-07 fault injection.",
        };
        var message = new AcsEmailMessage(Spike525AcsGate.SenderAddress, recipients, content);
        var operationId = Guid.NewGuid();

        Spike525AcsSendBudget.Reserve("S06-fault-injection");

        string classifiedErrorCode;
        bool classifiedRetryable;
        var faultTriggered = false;
        try
        {
            // WaitUntil.Started: exactly one HTTP call (the submission POST) so the fault
            // transport's "fault after call #1" targets the true submission boundary.
            await faultClient.SendAsync(WaitUntil.Started, message, operationId, ct);
            classifiedErrorCode = "none_no_exception_thrown";
            classifiedRetryable = false;
        }
        catch (Exception ex)
        {
            faultTriggered = true;
            (classifiedErrorCode, classifiedRetryable) = ProviderErrorClassifier.Classify(ex);
        }

        // S-07: re-query the SAME operation id via a NORMAL (non-faulty) client/transport —
        // independent ground truth for whether the request actually reached ACS despite the
        // local fault.
        var normalClient = BuildClient();
        var requeryOperation = new EmailSendOperation(operationId.ToString(), normalClient);
        var requerySucceeded = false;
        string requeryResolvedStatus = "not_resolved";
        for (var attempt = 0; attempt < 5 && !requerySucceeded; attempt++)
        {
            try
            {
                await requeryOperation.UpdateStatusAsync(ct);
                requerySucceeded = true;
                requeryResolvedStatus = requeryOperation.HasValue
                    ? requeryOperation.Value.Status.ToString()
                    : (requeryOperation.HasCompleted ? "completed_no_value" : "running");
                if (!requeryOperation.HasCompleted)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    requerySucceeded = false;
                }
            }
            catch (RequestFailedException ex)
            {
                requeryResolvedStatus = "requery_request_failed_status_" + ex.Status;
                break;
            }
        }

        Spike525Support.Evidence.Record("S-06-S-07", new
        {
            Provider = "acs",
            FaultPoint = "response-withheld-after-real-submission-post",
            LocalFaultTriggered = faultTriggered,
            ClassifiedErrorCode = classifiedErrorCode,
            ClassifiedRetryable = classifiedRetryable,
            OperationIdShortHash = Spike525Support.ShortHash(operationId.ToString()),
            RequeryConfirmsAcsHasRecord = requeryResolvedStatus != "not_resolved" && requeryResolvedStatus != "requery_request_failed_status_404",
            RequeryResolvedStatus = requeryResolvedStatus,
            OutcomeTaxonomy = "unknown_after_submission",
        });

        Assert.True(faultTriggered, "S-06 fault transport must inject a local fault after the real submission POST completes.");
    }

    [Fact]
    public async Task S01_S03_S04_S05_S08_S09_S10_S14_FullMatrixWithEventCorrelation()
    {
        if (!Spike525AcsGate.AcsLiveEnabled)
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var client = BuildClient();

        // --- Fixture: S-01 / S-03 / S-14 full product-boundary mapping (To1+CC9+BCC10=20) ---
        var boundaryTo = new List<AcsEmailAddress> { new(Spike525AcsGate.RecipientAddress) };
        var boundaryCc = Enumerable.Range(1, 9).Select(i => new AcsEmailAddress(SyntheticInvalidAddress($"s01-cc{i}"))).ToList();
        var boundaryBcc = Enumerable.Range(1, 10).Select(i => new AcsEmailAddress(SyntheticInvalidAddress($"s01-bcc{i}"))).ToList();
        var boundaryRecipients = new AcsEmailRecipients(boundaryTo, boundaryCc, boundaryBcc);
        var boundaryContent = new AcsEmailContent("spike525-acs-s01-boundary")
        {
            PlainText = "spike525 ACS live fixture S-01/S-03/S-14 full boundary.",
        };
        var boundaryMessage = new AcsEmailMessage(Spike525AcsGate.SenderAddress, boundaryRecipients, boundaryContent);
        boundaryMessage.Attachments.Add(new AcsEmailAttachment("spike525-notice.pdf", "application/pdf", BinaryData.FromBytes(SyntheticPdfBytes())));

        Spike525AcsSendBudget.Reserve("S01-S03-S14-boundary");
        var boundaryOperationId = Guid.NewGuid();
        var boundaryStart = DateTimeOffset.UtcNow;
        var boundaryOp = await client.SendAsync(WaitUntil.Completed, boundaryMessage, boundaryOperationId, ct);
        var boundaryElapsedMs = (DateTimeOffset.UtcNow - boundaryStart).TotalMilliseconds;

        Spike525Support.Evidence.Record("S-01-S-03-S-14-boundary", new
        {
            Provider = "acs",
            InvocationCount = 1,
            ToCount = boundaryTo.Count,
            CcCount = boundaryCc.Count,
            BccCount = boundaryBcc.Count,
            TotalRecipients = boundaryTo.Count + boundaryCc.Count + boundaryBcc.Count,
            SyncStatus = boundaryOp.HasValue ? boundaryOp.Value.Status.ToString() : "unknown",
            OperationIdPresent = !string.IsNullOrEmpty(boundaryOp.Id),
            OperationIdEqualsSuppliedGuid = Guid.TryParse(boundaryOp.Id, out var parsedBoundaryId) && parsedBoundaryId == boundaryOperationId,
            OperationIdShortHash = Spike525Support.ShortHash(boundaryOp.Id),
            ElapsedMs = boundaryElapsedMs,
        });

        // --- Fixture: S-04 / S-05 partial acceptance (one role member on a non-existent domain) ---
        var partialTo = new List<AcsEmailAddress> { new(Spike525AcsGate.RecipientAddress) };
        var partialCc = new List<AcsEmailAddress> { new(SyntheticInvalidAddress("s05-cc1")) };
        var partialRecipients = new AcsEmailRecipients(partialTo, partialCc, []);
        var partialContent = new AcsEmailContent("spike525-acs-s04-s05")
        {
            PlainText = "spike525 ACS live fixture S-04/S-05 partial acceptance.",
        };
        var partialMessage = new AcsEmailMessage(Spike525AcsGate.SenderAddress, partialRecipients, partialContent);

        Spike525AcsSendBudget.Reserve("S04-S05-partial");
        var partialOperationId = Guid.NewGuid();
        var partialOp = await client.SendAsync(WaitUntil.Completed, partialMessage, partialOperationId, ct);
        var partialResultProperties = typeof(EmailSendResult).GetProperties().Select(p => p.Name).ToArray();

        Spike525Support.Evidence.Record("S-04-S-05-partial", new
        {
            Provider = "acs",
            Scenario = "one-real-recipient-plus-one-nonexistent-domain-cc",
            SyncResultExposedProperties = partialResultProperties,
            DetailGranularity = partialResultProperties.Any(p => p.Contains("Recipient", StringComparison.OrdinalIgnoreCase))
                ? "provider_specific"
                : "not_available",
            SyncStatus = partialOp.HasValue ? partialOp.Value.Status.ToString() : "unknown",
            OperationIdShortHash = Spike525Support.ShortHash(partialOp.Id),
            Note = "Azure.Communication.Email 1.1.0 EmailSendResult exposes only a message-level Status; no per-recipient accept/reject field exists in the SDK's sync/LRO result shape. Recipient-level accept/reject and any resulting bounce can only be observed asynchronously via Event Grid (see queue correlation below).",
        });

        // --- Fixture: S-08 duplicate-retry-by-operationId (idempotency probe) ---
        var dupTo = new List<AcsEmailAddress> { new(Spike525AcsGate.RecipientAddress) };
        var dupRecipients = new AcsEmailRecipients(dupTo);
        var dupContent = new AcsEmailContent("spike525-acs-s08")
        {
            PlainText = "spike525 ACS live fixture S-08 duplicate retry.",
        };
        var dupMessage = new AcsEmailMessage(Spike525AcsGate.SenderAddress, dupRecipients, dupContent);
        var dupOperationId = Guid.NewGuid();

        Spike525AcsSendBudget.Reserve("S08-duplicate-original");
        var dupOp1 = await client.SendAsync(WaitUntil.Completed, dupMessage, dupOperationId, ct);
        var dupOutcome1 = "accepted_status_" + (dupOp1.HasValue ? dupOp1.Value.Status.ToString() : "unknown");

        Spike525AcsSendBudget.Reserve("S08-duplicate-retry");
        string dupOutcome2;
        try
        {
            var dupOp2 = await client.SendAsync(WaitUntil.Completed, dupMessage, dupOperationId, ct);
            dupOutcome2 = "accepted_status_" + (dupOp2.HasValue ? dupOp2.Value.Status.ToString() : "unknown");
        }
        catch (RequestFailedException ex)
        {
            dupOutcome2 = "rejected_status_" + ex.Status;
        }

        Spike525Support.Evidence.Record("S-08-duplicate-retry", new
        {
            Provider = "acs",
            FirstAttemptOutcome = dupOutcome1,
            SecondAttemptSameOperationIdOutcome = dupOutcome2,
            ServerRejectedSecondAttempt = dupOutcome2.StartsWith("rejected_", StringComparison.Ordinal),
            OperationIdShortHash = Spike525Support.ShortHash(dupOperationId.ToString()),
            CanonicalRecommendation = dupOutcome2.StartsWith("rejected_", StringComparison.Ordinal)
                ? "Client-supplied operationId is enforced server-side as a uniqueness/idempotency key by ACS; a whole-request retry reusing the same deterministic operationId (as production AcsOperationIdFactory already computes) is rejected rather than silently re-sent."
                : "ACS accepted a second SendAsync call reusing the same client-supplied operationId without rejecting it; operationId reuse alone does not appear to prevent a second underlying send at the SDK/API layer. Event Grid correlation below records whether one or two delivery-report events resulted.",
        });

        // --- Event Grid / Storage Queue correlation for all operation ids sent in this run ---
        var operationIds = new (string FixtureId, Guid Id)[]
        {
            ("S-01-S-03-S-14-boundary", boundaryOperationId),
            ("S-04-S-05-partial", partialOperationId),
            ("S-08-duplicate (shared id)", dupOperationId),
        };

        var queueOptions = new MailerBounceIngestionOptions
        {
            QueueConnectionString = Spike525AcsGate.QueueConnectionString,
            QueueName = Spike525AcsGate.QueueName,
        };
        var queueClient = new AzureAcsEventQueueClient(queueOptions);

        var eventsByMessageId = new Dictionary<string, List<(string Status, string RecipientShortHash)>>(StringComparer.OrdinalIgnoreCase);
        var totalMessagesReceived = 0;
        var unparseableCount = 0;

        for (var round = 0; round < 6; round++)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<AcsQueueReceivedMessage> messages;
            try
            {
                messages = await queueClient.ReceiveMessagesAsync(32, TimeSpan.FromSeconds(30), ct);
            }
            catch (RequestFailedException)
            {
                messages = [];
            }

            foreach (var raw in messages)
            {
                totalMessagesReceived++;
                string body;
                try
                {
                    body = AcsQueueMessageBodyDecoder.Decode(raw.Body);
                }
                catch (Exception)
                {
                    unparseableCount++;
                    continue;
                }

                IReadOnlyList<AcsEventParseResult> parsed;
                try
                {
                    parsed = AcsEventParser.ParseMany(body);
                }
                catch (JsonException)
                {
                    unparseableCount++;
                    continue;
                }

                foreach (var result in parsed)
                {
                    if (result.Outcome == AcsEventParseOutcome.Unparseable)
                    {
                        unparseableCount++;
                        continue;
                    }

                    if (result.Outcome != AcsEventParseOutcome.DeliveryReport || result.Report is null)
                    {
                        continue;
                    }

                    var list = eventsByMessageId.TryGetValue(result.Report.MessageId, out var existing)
                        ? existing
                        : eventsByMessageId[result.Report.MessageId] = [];
                    list.Add((result.Report.Status, Spike525Support.ShortHash(result.Report.Recipient)));
                }
            }

            if (round < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(25), ct);
            }
        }

        foreach (var (fixtureId, operationId) in operationIds)
        {
            var matched = eventsByMessageId
                .Where(kvp => Guid.TryParse(kvp.Key, out var parsed) && parsed == operationId)
                .SelectMany(kvp => kvp.Value)
                .ToArray();

            Spike525Support.Evidence.Record("S-09-S-10-correlation", new
            {
                Provider = "acs",
                FixtureId = fixtureId,
                OperationIdShortHash = Spike525Support.ShortHash(operationId.ToString()),
                EventCountForThisMessageId = matched.Length,
                DistinctStatusesObserved = matched.Select(m => m.Status).Distinct().ToArray(),
                Note = matched.Length == 0
                    ? "No Event Grid delivery-report event observed within this Spike run's polling window (ADR 0020 F-7: ~2 minutes average). Not evidence of absence; SPIKE_INCONCLUSIVE for this operation id within this run."
                    : "See DistinctStatusesObserved for observed provider delivery outcome(s).",
            });
        }

        Spike525Support.Evidence.Record("S-09-S-10-queue-poll-summary", new
        {
            Provider = "acs",
            TotalQueueMessagesReceived = totalMessagesReceived,
            UnparseableCount = unparseableCount,
            DistinctMessageIdsObserved = eventsByMessageId.Count,
            MaxEventsForAnySingleMessageId = eventsByMessageId.Count == 0 ? 0 : eventsByMessageId.Values.Max(v => v.Count),
            Note = "Queue messages were received (temporarily invisible) but never deleted by this Spike — non-destructive read of a disposable non-production queue.",
        });

        Assert.True(boundaryOp.HasValue, "S-01/S-03/S-14 boundary send did not reach a terminal ACS status.");
        Assert.True(partialOp.HasValue, "S-04/S-05 partial-acceptance send did not reach a terminal ACS status.");
        Assert.True(dupOp1.HasValue, "S-08 first duplicate-retry send did not reach a terminal ACS status.");
    }

    private static AcsEmailClient BuildClient(HttpPipelineTransport? transport = null)
    {
        var options = new EmailClientOptions();
        if (transport is not null)
        {
            options.Transport = transport;
        }

        return new AcsEmailClient(Spike525AcsGate.AcsConnectionString, options);
    }

    private static string SyntheticInvalidAddress(string localPart) =>
        $"{localPart}@{Spike525Support.SyntheticInvalidDomain}";

    private static byte[] SyntheticPdfBytes() =>
        "%PDF-1.4\n% spike525 synthetic, not a real PDF structure\n%%EOF\n"u8.ToArray();

    private static byte[] SyntheticPngBytes() =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02, 0x03];
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Spike525AcsLiveCollection
{
    internal const string Name = "Spike525AcsLive";
}
