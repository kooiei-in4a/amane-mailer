using System.Text.Json;
using Azure;
using Azure.Communication.Email;
using Azure.Core.Pipeline;
using Amane.Mailer.Bounce;
using Amane.Mailer.Configuration;
using Amane.Mailer.Delivery;
using Amane.Mailer.Json;
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
/// <see cref="Spike525AcsGate.AcsLiveEnabled"/> is true. Every real send reserves a slot from
/// <see cref="Spike525AcsSendBudget"/> first (AMANE_ACS_SPIKE_MAX_SENDS).
///
/// This is a SINGLE consolidated test method (not split across several [Fact]s) so that every
/// send's operation id can be tracked in one shared list and correlated together against the
/// Event Grid / Storage Queue in one polling pass at the end. An earlier split-method design
/// under-correlated (only 3 of 9 operation ids were checked against the queue) — see the
/// post-ACS Agent B review on PR #529 (M-02). xunit does not guarantee cross-method execution
/// order, so cross-fixture correlation requires one method.
///
/// Deliberately test-only, greenfield ACS message construction (multi-recipient
/// EmailRecipients, attachments) — the production <see cref="AcsMailDeliveryProvider"/> only
/// supports a single recipient today, so it cannot be reused here without pre-committing a
/// public multi-recipient contract this Spike must not decide. Fault classification (S-06) DOES
/// reuse the real production <see cref="ProviderErrorClassifier"/>.
///
/// Evidence is value-free per #525 policy: no raw recipient/sender/BCC, no full operation or
/// message IDs (only <see cref="Spike525Support.ShortHash"/>), no raw Event Grid payload, no
/// connection strings/keys. Queue messages are received (temporarily invisible) but never
/// deleted — a non-destructive, reversible read of a disposable non-production queue. Event
/// Grid event ids are de-duplicated locally before counting (M-03: a message that becomes
/// visible again after its visibility timeout would otherwise be double-counted, since nothing
/// else is deleting messages from this dedicated Spike queue).
/// </summary>
[Collection(Spike525AcsLiveCollection.Name)]
public sealed class Spike525AcsLiveTests
{
    private sealed record ExpectedRecipient(string ShortHash, string Role, string FixtureId);

    private sealed record RawRoleEvent(string EventId, string MessageId, string Status, string RecipientShortHash);

    [Fact]
    public async Task AcsLiveFixtureMatrix()
    {
        if (!Spike525AcsGate.AcsLiveEnabled)
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var client = BuildClient();
        var realRecipientShortHash = Spike525Support.ShortHash(Spike525AcsGate.RecipientAddress);
        var expected = new List<ExpectedRecipient>();
        var operationIds = new List<(string FixtureId, Guid Id)>();

        AcsEmailAddress Track(string address, string role, string fixtureId)
        {
            expected.Add(new ExpectedRecipient(Spike525Support.ShortHash(address), role, fixtureId));
            return new AcsEmailAddress(address);
        }

        // ==================================================================
        // S-01 BCC-only (To empty)
        // ==================================================================
        string bccOnlyOutcome;
        AcsEmailRecipients? bccOnlyRecipients = null;
        try
        {
            bccOnlyRecipients = new AcsEmailRecipients(
                to: [],
                cc: [],
                bcc: [Track(Spike525AcsGate.RecipientAddress, "bcc", "S-01-bcc-only")]);
            bccOnlyOutcome = "client_side_constructor_accepted_empty_to";
        }
        catch (Exception ex)
        {
            bccOnlyOutcome = "client_side_constructor_threw_" + ex.GetType().Name;
        }

        if (bccOnlyRecipients is not null)
        {
            Spike525AcsSendBudget.Reserve("S01-bcc-only");
            var bccOperationId = Guid.NewGuid();
            var bccContent = new AcsEmailContent("spike525-acs-s01-bcc-only") { PlainText = "spike525 ACS live fixture S-01 bcc-only." };
            var bccMessage = new AcsEmailMessage(Spike525AcsGate.SenderAddress, bccOnlyRecipients, bccContent);
            try
            {
                var op = await client.SendAsync(WaitUntil.Completed, bccMessage, bccOperationId, ct);
                bccOnlyOutcome = "server_accepted_status_" + (op.HasValue ? op.Value.Status.ToString() : "unknown");
                operationIds.Add(("S-01-bcc-only", bccOperationId));
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
        });

        // ==================================================================
        // S-15 / S-16: multiple attachments incl. TXT and NFC Japanese filename
        // (M-04: TXT was missing from the prior run's mandatory attachment-type set)
        // ==================================================================
        var attachRecipients = new AcsEmailRecipients(to: [Track(Spike525AcsGate.RecipientAddress, "to", "S-15-S-16")]);
        var attachContent = new AcsEmailContent("spike525-acs-s15-s16") { PlainText = "spike525 ACS live fixture S-15/S-16 attachments." };
        var attachMessage = new AcsEmailMessage(Spike525AcsGate.SenderAddress, attachRecipients, attachContent);
        var japaneseFileName = "請求書.pdf".Normalize(System.Text.NormalizationForm.FormC);
        var attachmentNames = new[] { "a-first.pdf", "b-second.png", "c-third.txt", japaneseFileName };
        attachMessage.Attachments.Add(new AcsEmailAttachment("a-first.pdf", "application/pdf", BinaryData.FromBytes(SyntheticPdfBytes())));
        attachMessage.Attachments.Add(new AcsEmailAttachment("b-second.png", "image/png", BinaryData.FromBytes(SyntheticPngBytes())));
        attachMessage.Attachments.Add(new AcsEmailAttachment("c-third.txt", "text/plain", BinaryData.FromBytes(SyntheticTextBytes())));
        attachMessage.Attachments.Add(new AcsEmailAttachment(japaneseFileName, "application/pdf", BinaryData.FromBytes(SyntheticPdfBytes())));

        Spike525AcsSendBudget.Reserve("S15-S16-attachments");
        var attachOperationId = Guid.NewGuid();
        var attachOp = await client.SendAsync(WaitUntil.Completed, attachMessage, attachOperationId, ct);
        operationIds.Add(("S-15-S-16", attachOperationId));

        Spike525Support.Evidence.Record("S-15-S-16", new
        {
            Provider = "acs",
            AttachmentCount = attachMessage.Attachments.Count,
            AttachmentTypes = new[] { "pdf", "png", "txt", "pdf-nfc-japanese-filename" },
            AttachmentNamesAreNfc = attachmentNames.All(n => n.IsNormalized(System.Text.NormalizationForm.FormC)),
            SyncStatus = attachOp.HasValue ? attachOp.Value.Status.ToString() : "unknown",
            OperationIdShortHash = Spike525Support.ShortHash(attachOp.Id),
            TransportEncoding = "acs-rest-json-base64-inherent",
            Note = "ACS EmailAttachment.Content is BinaryData sent as base64 JSON on the wire; unlike MIME/SMTP there is no Content-Transfer-Encoding auto-selection, so the Mailpit S-15b LF/CRLF canonicalization gap structurally cannot occur here. Delivered-byte digest, delivered filename, delivered Content-Type, and delivered attachment order at the real mailbox were NOT independently verified in this environment (no inbox-read capability available to this Spike session) — SPIKE_INCONCLUSIVE for delivered-side attachment integrity per Agent B M-04.",
        });

        Assert.True(attachOp.HasValue, "S-15/S-16 attachment send did not reach a terminal ACS status.");

        // ==================================================================
        // S-06 / S-07: fault injection + operationId re-query
        // ==================================================================
        var faultTransport = new Spike525AcsFaultAfterSubmissionTransport();
        var faultClient = BuildClient(faultTransport);
        var faultRecipients = new AcsEmailRecipients(to: [Track(Spike525AcsGate.RecipientAddress, "to", "S-06-S-07")]);
        var faultContent = new AcsEmailContent("spike525-acs-s06-s07") { PlainText = "spike525 ACS live fixture S-06/S-07 fault injection." };
        var faultMessage = new AcsEmailMessage(Spike525AcsGate.SenderAddress, faultRecipients, faultContent);
        var faultOperationId = Guid.NewGuid();

        Spike525AcsSendBudget.Reserve("S06-fault-injection");
        operationIds.Add(("S-06-S-07", faultOperationId));

        string classifiedErrorCode;
        bool classifiedRetryable;
        var faultTriggered = false;
        try
        {
            await faultClient.SendAsync(WaitUntil.Started, faultMessage, faultOperationId, ct);
            classifiedErrorCode = "none_no_exception_thrown";
            classifiedRetryable = false;
        }
        catch (Exception ex)
        {
            faultTriggered = true;
            (classifiedErrorCode, classifiedRetryable) = ProviderErrorClassifier.Classify(ex);
        }

        var requeryOperation = new EmailSendOperation(faultOperationId.ToString(), client);

        // M-02 fix: only a genuinely successful (non-exception) UpdateStatusAsync call — of any
        // resolved status, terminal or not — counts as confirmation that ACS has a record of the
        // operation. A RequestFailedException at ANY status (401/403/404/429/500/...) must NOT be
        // treated as confirmation; the prior version only special-cased 404, which silently
        // treated other error statuses as "confirmed" too.
        var requeryReachedAcsWithoutError = false;
        string requeryResolvedStatus = "not_resolved";
        for (var attempt = 0; attempt < 5 && requeryResolvedStatus is "not_resolved" or "running"; attempt++)
        {
            try
            {
                await requeryOperation.UpdateStatusAsync(ct);
                requeryReachedAcsWithoutError = true;
                requeryResolvedStatus = requeryOperation.HasValue
                    ? requeryOperation.Value.Status.ToString()
                    : (requeryOperation.HasCompleted ? "completed_no_value" : "running");
                if (requeryResolvedStatus == "running")
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
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
            OperationIdShortHash = Spike525Support.ShortHash(faultOperationId.ToString()),
            RequeryConfirmsAcsHasRecord = requeryReachedAcsWithoutError,
            RequeryResolvedStatus = requeryResolvedStatus,
            OutcomeTaxonomy = "unknown_after_submission",
        });

        Assert.True(faultTriggered, "S-06 fault transport must inject a local fault after the real submission POST completes.");

        // ==================================================================
        // S-01 / S-03 / S-14: full product-boundary mapping (To1+CC9+BCC10=20)
        // ==================================================================
        var boundaryTo = new List<AcsEmailAddress> { Track(Spike525AcsGate.RecipientAddress, "to", "S-01-S-03-S-14-boundary") };
        var boundaryCc = Enumerable.Range(1, 9).Select(i => Track(SyntheticInvalidAddress($"s01-cc{i}"), "cc", "S-01-S-03-S-14-boundary")).ToList();
        var boundaryBcc = Enumerable.Range(1, 10).Select(i => Track(SyntheticInvalidAddress($"s01-bcc{i}"), "bcc", "S-01-S-03-S-14-boundary")).ToList();
        var boundaryRecipients = new AcsEmailRecipients(boundaryTo, boundaryCc, boundaryBcc);
        var boundaryContent = new AcsEmailContent("spike525-acs-s01-boundary") { PlainText = "spike525 ACS live fixture S-01/S-03/S-14 To1/CC9/BCC10." };
        var boundaryMessage = new AcsEmailMessage(Spike525AcsGate.SenderAddress, boundaryRecipients, boundaryContent);
        boundaryMessage.Attachments.Add(new AcsEmailAttachment("spike525-notice.pdf", "application/pdf", BinaryData.FromBytes(SyntheticPdfBytes())));

        Spike525AcsSendBudget.Reserve("S01-S03-S14-boundary");
        var boundaryOperationId = Guid.NewGuid();
        var boundaryStart = DateTimeOffset.UtcNow;
        var boundaryOp = await client.SendAsync(WaitUntil.Completed, boundaryMessage, boundaryOperationId, ct);
        var boundaryElapsedMs = (DateTimeOffset.UtcNow - boundaryStart).TotalMilliseconds;
        operationIds.Add(("S-01-S-03-S-14-boundary", boundaryOperationId));

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

        // ==================================================================
        // S-14 mandatory matrix: the 3 remaining required role-limit combinations
        // (M-01: only To1/CC9/BCC10 and BCC-only-1 were previously executed against live ACS)
        // ==================================================================
        async Task<(bool Accepted, Guid OperationId)> SendMatrixFixtureAsync(string fixtureId, int toCount, int ccCount, int bccCount)
        {
            var to = new List<AcsEmailAddress>();
            if (toCount > 0)
            {
                to.Add(Track(Spike525AcsGate.RecipientAddress, "to", fixtureId));
                for (var i = 1; i < toCount; i++)
                {
                    to.Add(Track(SyntheticInvalidAddress($"{fixtureId}-to{i}"), "to", fixtureId));
                }
            }

            var cc = new List<AcsEmailAddress>();
            if (ccCount > 0)
            {
                var start = toCount == 0 ? 1 : 0;
                if (toCount == 0)
                {
                    cc.Add(Track(Spike525AcsGate.RecipientAddress, "cc", fixtureId));
                }

                for (var i = start; i < ccCount; i++)
                {
                    cc.Add(Track(SyntheticInvalidAddress($"{fixtureId}-cc{i}"), "cc", fixtureId));
                }
            }

            var bcc = Enumerable.Range(0, bccCount)
                .Select(i => Track(SyntheticInvalidAddress($"{fixtureId}-bcc{i}"), "bcc", fixtureId))
                .ToList();

            var recipients = new AcsEmailRecipients(to, cc, bcc);
            var content = new AcsEmailContent("spike525-acs-" + fixtureId) { PlainText = "spike525 ACS live fixture " + fixtureId + "." };
            var message = new AcsEmailMessage(Spike525AcsGate.SenderAddress, recipients, content);

            Spike525AcsSendBudget.Reserve(fixtureId);
            var operationId = Guid.NewGuid();
            var op = await client.SendAsync(WaitUntil.Completed, message, operationId, ct);
            operationIds.Add((fixtureId, operationId));

            Spike525Support.Evidence.Record(fixtureId, new
            {
                Provider = "acs",
                ToCount = to.Count,
                CcCount = cc.Count,
                BccCount = bcc.Count,
                TotalRecipients = to.Count + cc.Count + bcc.Count,
                SyncStatus = op.HasValue ? op.Value.Status.ToString() : "unknown",
                OperationIdShortHash = Spike525Support.ShortHash(op.Id),
            });

            return (op.HasValue, operationId);
        }

        var matrix1 = await SendMatrixFixtureAsync("S14-matrix-to10-cc10-bcc0", 10, 10, 0);
        var matrix2 = await SendMatrixFixtureAsync("S14-matrix-to10-cc0-bcc10", 10, 0, 10);
        var matrix3 = await SendMatrixFixtureAsync("S14-matrix-to0-cc10-bcc10", 0, 10, 10);

        Assert.True(matrix1.Accepted, "S-14 matrix To10/CC10/BCC0 did not reach a terminal ACS status.");
        Assert.True(matrix2.Accepted, "S-14 matrix To10/CC0/BCC10 did not reach a terminal ACS status.");
        Assert.True(matrix3.Accepted, "S-14 matrix To0/CC10/BCC10 did not reach a terminal ACS status.");

        // ==================================================================
        // S-04 / S-05: partial acceptance (one real recipient + one non-existent-domain CC)
        // ==================================================================
        var partialTo = new List<AcsEmailAddress> { Track(Spike525AcsGate.RecipientAddress, "to", "S-04-S-05-partial") };
        var partialCc = new List<AcsEmailAddress> { Track(SyntheticInvalidAddress("s05-cc1"), "cc", "S-04-S-05-partial") };
        var partialRecipients = new AcsEmailRecipients(partialTo, partialCc, []);
        var partialContent = new AcsEmailContent("spike525-acs-s04-s05") { PlainText = "spike525 ACS live fixture S-04/S-05 partial acceptance." };
        var partialMessage = new AcsEmailMessage(Spike525AcsGate.SenderAddress, partialRecipients, partialContent);

        Spike525AcsSendBudget.Reserve("S04-S05-partial");
        var partialOperationId = Guid.NewGuid();
        var partialOp = await client.SendAsync(WaitUntil.Completed, partialMessage, partialOperationId, ct);
        operationIds.Add(("S-04-S-05-partial", partialOperationId));
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
            Note = "Azure.Communication.Email 1.1.0 EmailSendResult exposes only a message-level Status; recipient-level accept/reject can only be observed asynchronously via Event Grid (see role-level correlation below). CONFIRMED: not_available for sync recipient-level detail. Whether this message-level Succeeded genuinely represents a MIXED per-recipient outcome (one delivered, one bounced) rather than a uniform one is establishied only by the role-level event correlation below, per Agent B M-02.",
        });

        // ==================================================================
        // S-08: duplicate-retry-by-operationId (idempotency probe)
        // ==================================================================
        var dupTo = new List<AcsEmailAddress> { Track(Spike525AcsGate.RecipientAddress, "to", "S-08-duplicate") };
        var dupRecipients = new AcsEmailRecipients(dupTo);
        var dupContent = new AcsEmailContent("spike525-acs-s08") { PlainText = "spike525 ACS live fixture S-08 duplicate retry." };
        var dupMessage = new AcsEmailMessage(Spike525AcsGate.SenderAddress, dupRecipients, dupContent);
        var dupOperationId = Guid.NewGuid();

        Spike525AcsSendBudget.Reserve("S08-duplicate-original");
        var dupOp1 = await client.SendAsync(WaitUntil.Completed, dupMessage, dupOperationId, ct);
        operationIds.Add(("S-08-duplicate", dupOperationId));
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
                ? "Client-supplied operationId is enforced server-side as a uniqueness/idempotency key by ACS; a whole-request retry reusing the same deterministic operationId (as production AcsOperationIdFactory already computes) is rejected rather than silently re-sent. This protects only the exact-operationId-reuse case — it does not replace the no-automatic-retry policy for accepted/partially_accepted/unknown_after_submission generally."
                : "ACS accepted a second SendAsync call reusing the same client-supplied operationId without rejecting it.",
        });

        Assert.True(boundaryOp.HasValue, "S-01/S-03/S-14 boundary send did not reach a terminal ACS status.");
        Assert.True(partialOp.HasValue, "S-04/S-05 partial-acceptance send did not reach a terminal ACS status.");
        Assert.True(dupOp1.HasValue, "S-08 first duplicate-retry send did not reach a terminal ACS status.");

        // ==================================================================
        // Event Grid / Storage Queue correlation for EVERY operation id sent in this run
        // (M-02/M-03 fix: all 9 fixtures now included; events de-duplicated by Event Grid
        // event id before counting; role-level correlation via expected-recipient short-hash
        // mapping; raw/unfiltered status parsing so Delivered — which the production
        // AcsEventParser deliberately discards as Ignored, since production only tracks bounces
        // — is visible to this Spike's evidence.)
        // ==================================================================
        var queueOptions = new MailerBounceIngestionOptions
        {
            QueueConnectionString = Spike525AcsGate.QueueConnectionString,
            QueueName = Spike525AcsGate.QueueName,
        };
        var queueClient = new AzureAcsEventQueueClient(queueOptions);

        var seenEventIds = new HashSet<string>(StringComparer.Ordinal);
        var rawEventsByMessageId = new Dictionary<string, List<RawRoleEvent>>(StringComparer.OrdinalIgnoreCase);
        var totalMessagesReceived = 0;
        var duplicateQueueDeliveriesSkipped = 0;
        var unparseableCount = 0;

        for (var round = 0; round < 8; round++)
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

                List<RawRoleEvent> rawEvents;
                try
                {
                    rawEvents = ParseRawRoleEvents(body);
                }
                catch (JsonException)
                {
                    unparseableCount++;
                    continue;
                }

                foreach (var rawEvent in rawEvents)
                {
                    if (!seenEventIds.Add(rawEvent.EventId))
                    {
                        // Same Event Grid event id seen again — either the same queue message
                        // reappeared after its visibility timeout (this Spike never deletes
                        // messages from this dedicated queue), or Event Grid genuinely
                        // redelivered it. Either way it is NOT a new provider event and must not
                        // inflate cardinality evidence (Agent B M-03).
                        duplicateQueueDeliveriesSkipped++;
                        continue;
                    }

                    var list = rawEventsByMessageId.TryGetValue(rawEvent.MessageId, out var existing)
                        ? existing
                        : rawEventsByMessageId[rawEvent.MessageId] = [];
                    list.Add(rawEvent);
                }
            }

            if (round < 7)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
            }
        }

        foreach (var (fixtureId, operationId) in operationIds)
        {
            var operationIdString = operationId.ToString();
            var matched = rawEventsByMessageId
                .Where(kvp => Guid.TryParse(kvp.Key, out var parsed) && parsed == operationId)
                .SelectMany(kvp => kvp.Value)
                .ToArray();

            var expectedForFixture = expected.Where(e => e.FixtureId == fixtureId).ToList();
            var roleByRoleEvents = matched
                .Select(m =>
                {
                    var role = expectedForFixture.FirstOrDefault(e => e.ShortHash == m.RecipientShortHash)?.Role
                        ?? (m.RecipientShortHash == realRecipientShortHash ? "to-or-cc-real-unmapped" : "unmapped");
                    return new { m.Status, Role = role, IsRealRecipient = m.RecipientShortHash == realRecipientShortHash };
                })
                .ToArray();

            Spike525Support.Evidence.Record("S-09-S-10-correlation", new
            {
                Provider = "acs",
                FixtureId = fixtureId,
                OperationIdShortHash = Spike525Support.ShortHash(operationIdString),
                DedupedEventCountForThisMessageId = matched.Length,
                DistinctStatusesObserved = matched.Select(m => m.Status).Distinct().ToArray(),
                RealRecipientStatusObserved = roleByRoleEvents.Where(r => r.IsRealRecipient).Select(r => r.Status).Distinct().ToArray(),
                SyntheticRoleStatusObserved = roleByRoleEvents.Where(r => !r.IsRealRecipient).Select(r => new { r.Role, r.Status }).Distinct().ToArray(),
                Note = matched.Length == 0
                    ? "No Event Grid delivery-report event observed within this run's polling window (ADR 0020 F-7: ~2 minutes average) for this operation id. Not evidence of absence; SPIKE_INCONCLUSIVE for this operation id within this run."
                    : "Statuses include Delivered where observed (this Spike's raw parser does not discard Delivered the way production AcsEventParser does, since production only tracks bounces).",
            });
        }

        Spike525Support.Evidence.Record("S-09-S-10-queue-poll-summary", new
        {
            Provider = "acs",
            TotalQueueMessagesReceived = totalMessagesReceived,
            DuplicateQueueDeliveriesSkippedByEventIdDedup = duplicateQueueDeliveriesSkipped,
            UnparseableCount = unparseableCount,
            DistinctMessageIdsObserved = rawEventsByMessageId.Count,
            DistinctEventIdsObserved = seenEventIds.Count,
            OperationIdsIncludedInCorrelation = operationIds.Count,
            Note = "Queue messages were received (temporarily invisible) but never deleted by this Spike — non-destructive read of a disposable non-production queue. Event Grid event ids are de-duplicated locally before any cardinality claim (Agent B M-03); DuplicateQueueDeliveriesSkippedByEventIdDedup being nonzero would indicate the same event id was observed more than once across polling rounds (queue redelivery or genuine Event Grid at-least-once redelivery — this Spike cannot distinguish the two, so still SPIKE_INCONCLUSIVE for S-11 duplicate-event semantics specifically).",
        });
    }

    /// <summary>
    /// Spike-only, UNFILTERED Event Grid delivery-report parsing (Agent B M-02). Deliberately
    /// separate from the production <see cref="AcsEventParser"/>, which intentionally discards
    /// "Delivered" status events as <c>Ignored</c> (production only ingests bounces — see
    /// ADR 0020). This Spike needs positive delivery confirmation too, to distinguish which
    /// specific recipient/role a given event belongs to (S-04/S-05 partial-acceptance role-level
    /// evidence), so it deserializes with the same public, source-generated
    /// <see cref="MailerJsonContext"/> DTOs but keeps every status.
    /// </summary>
    private static List<RawRoleEvent> ParseRawRoleEvents(string decodedBody)
    {
        var results = new List<RawRoleEvent>();
        using var document = JsonDocument.Parse(decodedBody);
        var elements = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray()
            : Enumerable.Repeat(document.RootElement, 1);

        foreach (var element in elements)
        {
            Bounce.AcsEventGridEventDto? dto;
            try
            {
                dto = element.Deserialize(MailerJsonContext.Default.AcsEventGridEventDto);
            }
            catch (JsonException)
            {
                continue;
            }

            if (dto?.Data is null
                || string.IsNullOrWhiteSpace(dto.Id)
                || string.IsNullOrWhiteSpace(dto.Data.MessageId)
                || string.IsNullOrWhiteSpace(dto.Data.Status))
            {
                continue;
            }

            if (!string.Equals(dto.EventType, AcsEventParser.EmailDeliveryReportReceivedEventType, StringComparison.Ordinal))
            {
                continue;
            }

            results.Add(new RawRoleEvent(dto.Id, dto.Data.MessageId, dto.Data.Status, Spike525Support.ShortHash(dto.Data.Recipient)));
        }

        return results;
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

    private static byte[] SyntheticTextBytes() =>
        "spike525 synthetic attachment text content\n"u8.ToArray();
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Spike525AcsLiveCollection
{
    internal const string Name = "Spike525AcsLive";
}
