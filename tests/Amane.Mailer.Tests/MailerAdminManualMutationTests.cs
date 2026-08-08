using System.Net;
using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

[Collection(MailerTestCollection.Name)]
public sealed class MailerAdminManualMutationTests(MailerAdminFixture fixture)
    : IClassFixture<MailerAdminFixture>, IAsyncLifetime
{
    private static readonly Guid TenantA = MailerWebApplicationFixtureBase.TenantId;
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000202");

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
        fixture.Factory.Services.GetRequiredService<AdminLoginThrottle>().Clear();
        fixture.Factory.Services.GetRequiredService<AdminSessionExpiredDedupe>().Clear();
        fixture.Factory.Services.GetRequiredService<AdminDeadLetterCountCache>().ClearForTests();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Manual_retry_requeues_deadlettered_request_via_admin_post()
    {
        var ct = TestContext.Current.CancellationToken;
        var internalId = await SeedMailRequestAsync(MailRequestState.DeadLettered, TenantA, ct);

        var client = CreateClient();
        await LoginAsync(client, ct);
        var csrf = await ReadCsrfTokenFromAdminPageAsync(client, $"/admin/mail-requests/{internalId:D}", ct);

        using var response = await client.PostAsync(
            $"/admin/mail-requests/{internalId:D}/retry",
            CreateCsrfContent(csrf),
            ct);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal(
            $"/admin/mail-requests/{internalId:D}",
            response.Headers.Location?.OriginalString);

        var state = await ReadStatusAsync(internalId, ct);
        Assert.Equal(MailRequestState.Queued, state.Status);
        Assert.Equal(0, state.AttemptCount);
        Assert.Null(state.NextAttemptAt);

        var audit = await ReadLatestAuditAsync(internalId, AdminAuditLog.EventTypes.ManualRetryRequested, ct);
        Assert.NotNull(audit);
        Assert.Equal(AdminAuditLog.Results.Success, audit.Value.Result);
        Assert.DoesNotContain("user@example.com", audit.Value.EventType, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manual_cancel_marks_queued_request_as_cancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        var internalId = await SeedMailRequestAsync(MailRequestState.Queued, TenantA, ct);

        var client = CreateClient();
        await LoginAsync(client, ct);
        var csrf = await ReadCsrfTokenFromAdminPageAsync(client, $"/admin/mail-requests/{internalId:D}", ct);

        using var response = await client.PostAsync(
            $"/admin/mail-requests/{internalId:D}/cancel",
            CreateCsrfContent(csrf),
            ct);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);

        var state = await ReadStatusAsync(internalId, ct);
        Assert.Equal(MailRequestState.Cancelled, state.Status);
        Assert.Equal(MailRequestRepository.OperatorCancelledLastErrorMessage, state.LastErrorMessage);
        Assert.NotNull(state.CompletedAt);

        var audit = await ReadLatestAuditAsync(internalId, AdminAuditLog.EventTypes.ManualCancelRequested, ct);
        Assert.NotNull(audit);
        Assert.Equal(AdminAuditLog.Results.Success, audit.Value.Result);
    }

    [Fact]
    public async Task Manual_retry_returns_not_found_for_out_of_scope_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedScopedAdminAsync([TenantA], ct);
        _ = await SeedMailRequestAsync(MailRequestState.DeadLettered, TenantA, ct);
        var internalId = await SeedMailRequestAsync(MailRequestState.DeadLettered, TenantB, ct);

        var client = CreateClient();
        await LoginAsync(client, "scoped-admin", "scoped-admin-password", ct);
        var csrf = await ReadCsrfTokenFromAdminPageAsync(client, "/admin/dead-letters", ct);

        using var response = await client.PostAsync(
            $"/admin/mail-requests/{internalId:D}/retry",
            CreateCsrfContent(csrf),
            ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var audit = await ReadLatestAuditAsync(internalId, AdminAuditLog.EventTypes.ManualRetryRequested, ct);
        Assert.NotNull(audit);
        Assert.Equal(AdminAuditLog.Results.Failure, audit.Value.Result);
        Assert.Equal(AdminAuditLog.ErrorCodes.NotFound, audit.Value.ErrorCode);
    }

    [Fact]
    public async Task Manual_cancel_returns_conflict_when_processing_lock_is_active()
    {
        var ct = TestContext.Current.CancellationToken;
        var internalId = await SeedProcessingWithActiveLockAsync(TenantA, ct);
        _ = await SeedMailRequestAsync(MailRequestState.DeadLettered, TenantA, ct);

        var client = CreateClient();
        await LoginAsync(client, ct);
        var csrf = await ReadCsrfTokenFromAdminPageAsync(client, "/admin/dead-letters", ct);

        using var response = await client.PostAsync(
            $"/admin/mail-requests/{internalId:D}/cancel",
            CreateCsrfContent(csrf),
            ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var state = await ReadStatusAsync(internalId, ct);
        Assert.Equal(MailRequestState.Processing, state.Status);

        var audit = await ReadLatestAuditAsync(internalId, AdminAuditLog.EventTypes.ManualCancelRequested, ct);
        Assert.NotNull(audit);
        Assert.Equal(AdminAuditLog.ErrorCodes.LockHeld, audit.Value.ErrorCode);
    }

    [Theory]
    [InlineData(MailRequestState.DeadLettered)]
    [InlineData(MailRequestState.Failed)]
    [InlineData(MailRequestState.Delivered)]
    [InlineData(MailRequestState.Cancelled)]
    [InlineData(MailRequestState.DeliveryUnknown)]
    public async Task Manual_retry_rejects_attachment_request_from_every_terminal_state_with_fixed_reason_code(
        MailRequestState terminalStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        var internalId = await SeedAttachmentMailRequestAsync(terminalStatus, TenantA, ct);
        // A dummy non-attachment DeadLettered row guarantees /admin/dead-letters renders a
        // retry form regardless of which terminal status is under test in this iteration --
        // tokens are session-scoped, not tied to the page or row that issued them.
        await SeedMailRequestAsync(MailRequestState.DeadLettered, TenantA, ct);

        var client = CreateClient();
        await LoginAsync(client, ct);
        var csrf = await ReadCsrfTokenFromAdminPageAsync(client, "/admin/dead-letters", ct);

        using var response = await client.PostAsync(
            $"/admin/mail-requests/{internalId:D}/retry",
            CreateCsrfContent(csrf),
            ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("ATTACHMENT_MANUAL_RETRY_NOT_SUPPORTED", body, StringComparison.Ordinal);

        // Never mutated back to Queued: the request stays exactly as ADR 0022 D-08 requires.
        var state = await ReadStatusAsync(internalId, ct);
        Assert.Equal(terminalStatus, state.Status);

        var audit = await ReadLatestAuditAsync(internalId, AdminAuditLog.EventTypes.ManualRetryRequested, ct);
        Assert.NotNull(audit);
        Assert.Equal(AdminAuditLog.Results.Failure, audit.Value.Result);
        Assert.Equal(AdminAuditLog.ErrorCodes.AttachmentManualRetryNotSupported, audit.Value.ErrorCode);
    }

    [Fact]
    public async Task Manual_retry_rejects_plain_delivery_unknown_without_reinvoking_provider()
    {
        var ct = TestContext.Current.CancellationToken;
        var internalId = await SeedMailRequestAsync(MailRequestState.DeliveryUnknown, TenantA, ct);
        var now = SqliteTime.UtcNow;

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
            var auditRepository = scope.ServiceProvider.GetRequiredService<AdminAuditRepository>();
            var retry = await repository.TryManualRetryAsync(
                internalId,
                allowedTenantIds: null,
                now,
                auditRepository,
                new AdminAuditEvent
                {
                    EventType = AdminAuditLog.EventTypes.ManualRetryRequested,
                    Actor = "delivery-unknown-test-admin",
                    OccurredAt = now,
                    TargetType = AdminAuditLog.TargetTypes.MailRequest,
                    TargetId = internalId.ToString("D"),
                    Result = AdminAuditLog.Results.Success,
                },
                ct);

            Assert.Equal(ManualMailRequestMutationStatus.InvalidState, retry.Status);
        }

        var state = await ReadStatusAsync(internalId, ct);
        Assert.Equal(MailRequestState.DeliveryUnknown, state.Status);
        var audit = await ReadLatestAuditAsync(internalId, AdminAuditLog.EventTypes.ManualRetryRequested, ct);
        Assert.NotNull(audit);
        Assert.Equal(AdminAuditLog.Results.Failure, audit.Value.Result);
        Assert.Equal(AdminAuditLog.ErrorCodes.InvalidState, audit.Value.ErrorCode);
    }

    [Fact]
    public async Task Manual_cancel_allows_queued_attachment_request_without_submission_evidence()
    {
        var ct = TestContext.Current.CancellationToken;
        var internalId = await SeedAttachmentMailRequestAsync(MailRequestState.Queued, TenantA, ct);

        var client = CreateClient();
        await LoginAsync(client, ct);
        var csrf = await ReadCsrfTokenFromAdminPageAsync(client, $"/admin/mail-requests/{internalId:D}", ct);

        using var response = await client.PostAsync(
            $"/admin/mail-requests/{internalId:D}/cancel",
            CreateCsrfContent(csrf),
            ct);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        var state = await ReadStatusAsync(internalId, ct);
        Assert.Equal(MailRequestState.Cancelled, state.Status);
    }

    [Fact]
    public async Task Manual_cancel_rejects_attachment_request_once_submission_evidence_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        // Expired lease so the *only* thing standing between this row and an ADR 0015-style
        // stale-Processing cancel is the ADR 0022 D-08 submission-evidence boundary.
        var internalId = await SeedAttachmentMailRequestAsync(
            MailRequestState.Processing,
            TenantA,
            ct,
            lockExpiresAt: SqliteTime.UtcNow.AddMinutes(-5),
            withSubmissionEvidence: true);

        var client = CreateClient();
        await LoginAsync(client, ct);
        var csrf = await ReadCsrfTokenFromAdminPageAsync(client, $"/admin/mail-requests/{internalId:D}", ct);

        using var response = await client.PostAsync(
            $"/admin/mail-requests/{internalId:D}/cancel",
            CreateCsrfContent(csrf),
            ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var state = await ReadStatusAsync(internalId, ct);
        Assert.Equal(MailRequestState.Processing, state.Status);
    }

    [Fact]
    public async Task Attachment_filename_is_masked_by_default_and_reveal_is_audited()
    {
        var ct = TestContext.Current.CancellationToken;
        const string rawFileName = "quarterly-report.pdf";
        var internalId = await SeedAttachmentMailRequestWithMetadataAsync(
            MailRequestState.Delivered, TenantA, rawFileName, ct);

        var client = CreateClient();
        await LoginAsync(client, ct);

        using var detailResponse = await client.GetAsync($"/admin/mail-requests/{internalId:D}", ct);
        var detailHtml = await detailResponse.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        Assert.DoesNotContain(rawFileName, detailHtml, StringComparison.Ordinal);
        Assert.Contains("q***.pdf", detailHtml, StringComparison.Ordinal);
        Assert.Contains($"/admin/mail-requests/{internalId:D}/attachments/0/filename", detailHtml, StringComparison.Ordinal);

        using var revealResponse = await client.GetAsync(
            $"/admin/mail-requests/{internalId:D}/attachments/0/filename", ct);
        var revealHtml = await revealResponse.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, revealResponse.StatusCode);
        Assert.Contains(rawFileName, revealHtml, StringComparison.Ordinal);

        var audit = await ReadLatestAuditAsync(
            internalId, AdminAuditLog.EventTypes.AttachmentFilenameRevealed, ct);
        Assert.NotNull(audit);
        Assert.Equal(AdminAuditLog.Results.Success, audit.Value.Result);

        var fieldName = await ReadLatestAuditFieldNameAsync(
            internalId, AdminAuditLog.EventTypes.AttachmentFilenameRevealed, ct);
        Assert.Equal("attachments[0].file_name", fieldName);
        Assert.DoesNotContain(rawFileName, fieldName ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dead_lettered_attachment_request_hides_retry_and_cancel_actions()
    {
        var ct = TestContext.Current.CancellationToken;
        var internalId = await SeedAttachmentMailRequestAsync(MailRequestState.DeadLettered, TenantA, ct);

        var client = CreateClient();
        await LoginAsync(client, ct);

        using var response = await client.GetAsync($"/admin/mail-requests/{internalId:D}", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain($"/admin/mail-requests/{internalId:D}/retry", html, StringComparison.Ordinal);
        Assert.DoesNotContain($"/admin/mail-requests/{internalId:D}/cancel", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeliveryUnknown_status_renders_distinctly_from_failed_in_admin()
    {
        var ct = TestContext.Current.CancellationToken;
        var internalId = await SeedAttachmentMailRequestAsync(MailRequestState.DeliveryUnknown, TenantA, ct);

        var client = CreateClient();
        await LoginAsync(client, ct);

        using var detailResponse = await client.GetAsync($"/admin/mail-requests/{internalId:D}", ct);
        var detailHtml = await detailResponse.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        Assert.Contains("status-deliveryunknown", detailHtml, StringComparison.Ordinal);
        Assert.Contains(">DeliveryUnknown<", detailHtml, StringComparison.Ordinal);

        using var listResponse = await client.GetAsync("/admin/mail-requests?status=deliveryunknown", ct);
        var listHtml = await listResponse.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Contains(internalId.ToString("D"), listHtml, StringComparison.Ordinal);
        Assert.Contains("status-deliveryunknown", listHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dead_letters_page_enables_retry_form()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();
        await LoginAsync(client, ct);

        var mailRequestId = Guid.NewGuid();
        await SeedMailRequestAsync(
            MailRequestState.DeadLettered,
            TenantA,
            ct,
            mailRequestId: mailRequestId);

        using var response = await client.GetAsync("/admin/dead-letters", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("/retry", html, StringComparison.Ordinal);
        Assert.DoesNotContain("手動再送は未実装です", html, StringComparison.Ordinal);
        Assert.Contains(mailRequestId.ToString("D"), html, StringComparison.Ordinal);
    }

    private HttpClient CreateClient() =>
        fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private static async Task LoginAsync(HttpClient client, CancellationToken cancellationToken) =>
        await LoginAsync(client, MailerAdminFixture.Username, MailerAdminFixture.Password, cancellationToken);

    private static async Task LoginAsync(
        HttpClient client,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var csrfToken = await ReadCsrfTokenFromLoginAsync(client, cancellationToken);
        using var response = await client.PostAsync(
            "/admin/api/login",
            CreateLoginContent(csrfToken, username, password),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<string> ReadCsrfTokenFromLoginAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("/admin/login", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadCsrfTokenFromHtmlAsync(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static async Task<string> ReadCsrfTokenFromAdminPageAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadCsrfTokenFromHtmlAsync(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static Task<string> ReadCsrfTokenFromHtmlAsync(string html)
    {
        const string marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Admin page did not contain a CSRF token.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end > start, "Admin page CSRF token value was empty.");
        return Task.FromResult(html[start..end]);
    }

    private static FormUrlEncodedContent CreateCsrfContent(string csrfToken) =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrfToken,
        });

    private static FormUrlEncodedContent CreateLoginContent(string csrfToken, string username, string password) =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrfToken,
            ["username"] = username,
            ["password"] = password,
        });

    private async Task<Guid> SeedMailRequestAsync(
        MailRequestState status,
        Guid tenantId,
        CancellationToken cancellationToken,
        Guid? mailRequestId = null)
    {
        var internalId = Guid.NewGuid();
        var now = SqliteTime.UtcNow;
        var nowStorage = SqliteTime.ToStorageUtc(now);
        var completedAt = status is MailRequestState.Delivered
            or MailRequestState.Failed
            or MailRequestState.DeadLettered
            or MailRequestState.Cancelled
            ? nowStorage
            : (string?)null;

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts,
                accepted_at, created_at, updated_at, completed_at, last_error_message)
            VALUES (
                @Id, @TenantId, 'manual-mutation-test', @MailRequestId, 'test',
                '{}', @PayloadHash, 'subject', 'user@example.com',
                @Status, @AttemptCount, 3,
                @Now, @Now, @Now, @CompletedAt, @LastErrorMessage);
            """;
        command.Parameters.AddWithValue("@Id", internalId.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@MailRequestId", (mailRequestId ?? Guid.NewGuid()).ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('a', 64));
        command.Parameters.AddWithValue("@Status", (int)status);
        command.Parameters.AddWithValue("@AttemptCount", status == MailRequestState.DeadLettered ? 3 : 0);
        command.Parameters.AddWithValue("@Now", nowStorage);
        command.Parameters.AddWithValue("@CompletedAt", (object?)completedAt ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@LastErrorMessage",
            status == MailRequestState.DeadLettered ? "provider failed" : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return internalId;
    }

    private async Task<Guid> SeedProcessingWithActiveLockAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var internalId = Guid.NewGuid();
        var now = SqliteTime.UtcNow;
        var lockExpiresAt = now.AddMinutes(5);

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts, lock_token, lock_expires_at,
                accepted_at, created_at, updated_at)
            VALUES (
                @Id, @TenantId, 'manual-mutation-test', @MailRequestId, 'test',
                '{}', @PayloadHash, 'subject', 'user@example.com',
                @Status, 1, 3, @LockToken, @LockExpiresAt,
                @Now, @Now, @Now);
            """;
        command.Parameters.AddWithValue("@Id", internalId.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('b', 64));
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.Processing);
        command.Parameters.AddWithValue("@LockToken", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(lockExpiresAt));
        command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return internalId;
    }

    private async Task<Guid> SeedAttachmentMailRequestAsync(
        MailRequestState status,
        Guid tenantId,
        CancellationToken cancellationToken,
        DateTimeOffset? lockExpiresAt = null,
        bool withSubmissionEvidence = false)
    {
        var internalId = Guid.NewGuid();
        var now = SqliteTime.UtcNow;
        var nowStorage = SqliteTime.ToStorageUtc(now);
        var completedAt = status is MailRequestState.Delivered
            or MailRequestState.Failed
            or MailRequestState.DeadLettered
            or MailRequestState.Cancelled
            or MailRequestState.DeliveryUnknown
            ? nowStorage
            : (string?)null;
        var lockToken = Guid.NewGuid();

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO mail_requests (
                    id, tenant_id, source_service, mail_request_id, purpose,
                    payload_json, payload_hash, subject, recipient_email,
                    status, attempt_count, max_attempts, attachment_count,
                    lock_token, lock_expires_at,
                    accepted_at, created_at, updated_at, completed_at, last_error_message)
                VALUES (
                    @Id, @TenantId, 'manual-mutation-test', @MailRequestId, 'test',
                    '{}', @PayloadHash, 'subject', 'user@example.com',
                    @Status, @AttemptCount, 3, 1,
                    @LockToken, @LockExpiresAt,
                    @Now, @Now, @Now, @CompletedAt, @LastErrorMessage);
                """;
            command.Parameters.AddWithValue("@Id", internalId.ToString("D"));
            command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
            command.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("@PayloadHash", new string('c', 64));
            command.Parameters.AddWithValue("@Status", (int)status);
            command.Parameters.AddWithValue("@AttemptCount", status == MailRequestState.DeadLettered ? 3 : 1);
            command.Parameters.AddWithValue(
                "@LockToken",
                status == MailRequestState.Processing ? lockToken.ToString("D") : (object)DBNull.Value);
            command.Parameters.AddWithValue(
                "@LockExpiresAt",
                status == MailRequestState.Processing
                    ? SqliteTime.ToStorageUtc(lockExpiresAt ?? now.AddMinutes(5))
                    : (object)DBNull.Value);
            command.Parameters.AddWithValue("@Now", nowStorage);
            command.Parameters.AddWithValue("@CompletedAt", (object?)completedAt ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "@LastErrorMessage",
                status == MailRequestState.DeadLettered ? "provider failed" : DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (withSubmissionEvidence)
        {
            await using var evidenceCommand = connection.CreateCommand();
            evidenceCommand.CommandText = """
                INSERT INTO mail_attachment_submissions (
                    request_id, submission_state, provider, submission_started_at,
                    lock_token, created_at, updated_at)
                VALUES (@RequestId, 0, 'mailpit', @Now, @LockToken, @Now, @Now);
                """;
            evidenceCommand.Parameters.AddWithValue("@RequestId", internalId.ToString("D"));
            evidenceCommand.Parameters.AddWithValue("@Now", nowStorage);
            evidenceCommand.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
            await evidenceCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return internalId;
    }

    private async Task<Guid> SeedAttachmentMailRequestWithMetadataAsync(
        MailRequestState status,
        Guid tenantId,
        string fileName,
        CancellationToken cancellationToken)
    {
        var internalId = await SeedAttachmentMailRequestAsync(status, tenantId, cancellationToken);

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_request_attachments (
                id, request_id, attachment_order, file_name, content_type,
                byte_length, content_sha256, spool_key, created_at)
            VALUES (
                @Id, @RequestId, 0, @FileName, 'application/pdf',
                1024, @Sha256, @SpoolKey, @Now);
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@RequestId", internalId.ToString("D"));
        command.Parameters.AddWithValue("@FileName", fileName);
        command.Parameters.AddWithValue("@Sha256", new string('e', 64));
        command.Parameters.AddWithValue("@SpoolKey", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(SqliteTime.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return internalId;
    }

    private async Task SeedScopedAdminAsync(IReadOnlyList<Guid> tenantIds, CancellationToken cancellationToken) =>
        await fixture.Factory.Services.GetRequiredService<AdminUserRepository>()
            .CreateOrUpdateScopedUserAsync(
                "scoped-admin",
                AdminPasswordHasher.Hash("scoped-admin-password"),
                tenantIds,
                cancellationToken);

    private async Task<(MailRequestState Status, int AttemptCount, string? NextAttemptAt, string? LastErrorMessage, string? CompletedAt)> ReadStatusAsync(
        Guid internalId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, attempt_count, next_attempt_at, last_error_message, completed_at
            FROM mail_requests
            WHERE id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", internalId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return (
            (MailRequestState)reader.GetInt32(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private async Task<(string EventType, string Result, string? ErrorCode)?> ReadLatestAuditAsync(
        Guid internalId,
        string eventType,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_type, result, error_code
            FROM admin_audit_events
            WHERE target_id = @TargetId AND event_type = @EventType
            ORDER BY id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@TargetId", internalId.ToString("D"));
        command.Parameters.AddWithValue("@EventType", eventType);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return (
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private async Task<string?> ReadLatestAuditFieldNameAsync(
        Guid internalId,
        string eventType,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT field_name
            FROM admin_audit_events
            WHERE target_id = @TargetId AND event_type = @EventType
            ORDER BY id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@TargetId", internalId.ToString("D"));
        command.Parameters.AddWithValue("@EventType", eventType);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return reader.IsDBNull(0) ? null : reader.GetString(0);
    }
}
