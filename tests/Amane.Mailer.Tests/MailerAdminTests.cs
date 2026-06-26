using System.Net;
using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

[Collection(MailerTestCollection.Name)]
public sealed class MailerAdminTests(MailerAdminFixture fixture)
    : IClassFixture<MailerAdminFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
        fixture.Factory.Services.GetRequiredService<AdminLoginThrottle>().Clear();
        fixture.Factory.Services.GetRequiredService<AdminDeadLetterCountCache>().ClearForTests();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void Allowed_local_address_matches_exact_address()
    {
        var allowed = MailerAdminExtensions.IsAllowedLocalAddress(
            IPAddress.Parse("192.0.2.10"),
            "192.0.2.10");

        Assert.True(allowed);
    }

    [Fact]
    public void Configured_loopback_allows_loopback_local_address()
    {
        var allowed = MailerAdminExtensions.IsAllowedLocalAddress(
            IPAddress.IPv6Loopback,
            "127.0.0.1");

        Assert.True(allowed);
    }

    [Fact]
    public void Configured_loopback_rejects_non_loopback_local_address()
    {
        var allowed = MailerAdminExtensions.IsAllowedLocalAddress(
            IPAddress.Parse("192.0.2.10"),
            "127.0.0.1");

        Assert.False(allowed);
    }

    [Fact]
    public void Configured_any_allows_non_null_local_address()
    {
        var allowed = MailerAdminExtensions.IsAllowedLocalAddress(
            IPAddress.Parse("192.0.2.10"),
            "0.0.0.0");

        Assert.True(allowed);
    }

    [Fact]
    public void Configured_ipv6_any_allows_non_null_ipv6_local_address()
    {
        var allowed = MailerAdminExtensions.IsAllowedLocalAddress(
            IPAddress.Parse("2001:db8::10"),
            "::");

        Assert.True(allowed);
    }

    [Fact]
    public void Null_local_address_is_rejected()
    {
        var allowed = MailerAdminExtensions.IsAllowedLocalAddress(
            requestLocalAddress: null,
            configuredAllowedLocalAddress: "0.0.0.0");

        Assert.False(allowed);
    }

    [Theory]
    [InlineData("AMANE_ADMIN_BIND")]
    [InlineData("MAILER_ADMIN_BIND")]
    public void Deprecated_admin_bind_aliases_still_configure_allowed_local_address(string key)
    {
        var options = LoadAdminOptions(new Dictionary<string, string?>
        {
            [key] = "0.0.0.0",
        });

        Assert.Equal("0.0.0.0", options.AllowedLocalAddress);
    }

    [Fact]
    public void Mailer_prefixed_allowed_local_address_env_var_configures_allowed_local_address()
    {
        var options = LoadAdminOptions(new Dictionary<string, string?>
        {
            ["MAILER_ADMIN_ALLOWED_LOCAL_ADDRESS"] = "192.0.2.10",
        });

        Assert.Equal("192.0.2.10", options.AllowedLocalAddress);
    }

    [Fact]
    public void Mailer_prefixed_allowed_local_address_env_var_takes_precedence_over_deprecated_alias()
    {
        var options = LoadAdminOptions(new Dictionary<string, string?>
        {
            ["MAILER_ADMIN_ALLOWED_LOCAL_ADDRESS"] = "192.0.2.10",
            ["AMANE_ADMIN_BIND"] = "0.0.0.0",
        });

        Assert.Equal("192.0.2.10", options.AllowedLocalAddress);
    }

    [Fact]
    public void New_allowed_local_address_env_var_takes_precedence_over_deprecated_alias()
    {
        var options = LoadAdminOptions(new Dictionary<string, string?>
        {
            ["AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS"] = "192.0.2.10",
            ["AMANE_ADMIN_BIND"] = "0.0.0.0",
            ["MAILER_ADMIN_BIND"] = "127.0.0.1",
        });

        Assert.Equal("192.0.2.10", options.AllowedLocalAddress);
    }

    [Fact]
    public void Invalid_allowed_local_address_validation_names_new_env_var()
    {
        var options = new MailerAdminOptions
        {
            Enabled = true,
            Username = "admin",
            PasswordHash = AdminPasswordHasher.Hash("password"),
            AllowedLocalAddress = "not-an-ip",
        };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_admin_returns_404()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var disabledFixture = new MailerApiFixture();
        await disabledFixture.InitializeAsync();
        using var client = CreateClient(disabledFixture.Factory);

        using var response = await client.GetAsync("/admin", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Enabled_admin_without_password_hash_fails_startup()
    {
        await using var missingHashFixture = new MailerAdminMissingHashFixture();
        await missingHashFixture.InitializeAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var client = CreateClient(missingHashFixture.Factory);
            using var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        });

        Assert.Contains("AMANE_ADMIN_PASSWORD_HASH", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unauthenticated_admin_home_redirects_to_login()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);

        using var response = await client.GetAsync("/admin", ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Unauthenticated_static_files_redirect_to_login()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);

        using var response = await client.GetAsync("/admin/admin.css", ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Login_page_uses_inline_styles_without_static_css()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);

        using var response = await client.GetAsync("/admin/login", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<style>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/admin/admin.css", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unauthenticated_mail_requests_redirects_to_login()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);

        using var response = await client.GetAsync("/admin/mail-requests", ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/admin/login", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_accepts_hash_password_and_redirects_to_admin_home()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);
        var csrfToken = await ReadCsrfTokenAsync(client, ct);

        using var login = await client.PostAsync(
            "/admin/api/login",
            CreateLoginContent(csrfToken, MailerAdminFixture.Username, MailerAdminFixture.Password),
            ct);
        using var adminHome = await client.GetAsync("/admin", ct);
        using var stylesheet = await client.GetAsync("/admin/admin.css", ct);

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/admin", login.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, adminHome.StatusCode);
        Assert.Equal("/admin/mail-requests", adminHome.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, stylesheet.StatusCode);
    }

    [Fact]
    public async Task Login_requires_csrf_token()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);

        using var response = await client.PostAsync(
            "/admin/api/login",
            CreateLoginContent("missing", MailerAdminFixture.Username, MailerAdminFixture.Password),
            ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_locks_out_after_five_failures()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);
        var csrfToken = await ReadCsrfTokenAsync(client, ct);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var response = await client.PostAsync(
                "/admin/api/login",
                CreateLoginContent(csrfToken, MailerAdminFixture.Username, "wrong-password"),
                ct);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var locked = await client.PostAsync(
            "/admin/api/login",
            CreateLoginContent(csrfToken, MailerAdminFixture.Username, "wrong-password"),
            ct);
        using var stillLocked = await client.PostAsync(
            "/admin/api/login",
            CreateLoginContent(csrfToken, MailerAdminFixture.Username, MailerAdminFixture.Password),
            ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal("30", locked.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.TooManyRequests, stillLocked.StatusCode);
    }

    [Fact]
    public async Task Admin_hash_password_command_generates_verifiable_hash()
    {
        var input = new StringReader($"{MailerAdminFixture.Password}{Environment.NewLine}{MailerAdminFixture.Password}{Environment.NewLine}");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MailerCliHost.RunAdminHashPasswordAsync(
            ["admin", "hash-password"],
            input,
            output,
            error);

        var hash = output.ToString().Trim();

        Assert.Equal(0, exitCode);
        Assert.Contains("Password:", error.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("pbkdf2:sha256:", hash, StringComparison.Ordinal);
        Assert.True(AdminPasswordHasher.Verify(MailerAdminFixture.Password, hash));
    }

    [Fact]
    public async Task Mail_requests_list_filters_and_masks_pii()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);
        var visibleId = await SeedMailRequestAsync(
            sourceService: MailerWebApplicationFixtureBase.SourceService,
            status: MailRequestState.Queued,
            updatedAt: now,
            recipientEmail: "user@example.com",
            subject: "Sensitive Subject ABC",
            ct);
        var wrongStatusId = await SeedMailRequestAsync(
            sourceService: MailerWebApplicationFixtureBase.SourceService,
            status: MailRequestState.Delivered,
            updatedAt: now.AddMinutes(-1),
            recipientEmail: "delivered@example.com",
            subject: "Delivered Subject",
            ct);
        var wrongSourceId = await SeedMailRequestAsync(
            sourceService: "other",
            status: MailRequestState.Queued,
            updatedAt: now.AddMinutes(-2),
            recipientEmail: "other@example.com",
            subject: "Other Subject",
            ct);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var response = await client.GetAsync(
            $"/admin/mail-requests?status=queued&tenant_id={MailerWebApplicationFixtureBase.TenantId:D}&source_service={MailerWebApplicationFixtureBase.SourceService}",
            ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(visibleId.ToString("D"), html, StringComparison.Ordinal);
        Assert.Contains(MailerWebApplicationFixtureBase.TenantId.ToString("D"), html, StringComparison.Ordinal);
        Assert.Contains(MailerWebApplicationFixtureBase.SourceService, html, StringComparison.Ordinal);
        Assert.Contains("u***@example.com", html, StringComparison.Ordinal);
        Assert.Contains("Sensitive Su...", html, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Sensitive Subject ABC", html, StringComparison.Ordinal);
        Assert.DoesNotContain(wrongStatusId.ToString("D"), html, StringComparison.Ordinal);
        Assert.DoesNotContain(wrongSourceId.ToString("D"), html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mail_requests_list_shows_empty_state()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var response = await client.GetAsync("/admin/mail-requests", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("送信依頼がありません", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mail_requests_list_pages_with_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 55; i++)
        {
            await SeedMailRequestAsync(
                sourceService: MailerWebApplicationFixtureBase.SourceService,
                status: MailRequestState.Queued,
                updatedAt: now.AddMinutes(-i),
                recipientEmail: $"page-{i:D2}@example.com",
                subject: $"Page {i:D2} Subject",
                ct);
        }

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var first = await client.GetAsync("/admin/mail-requests", ct);
        var firstHtml = await first.Content.ReadAsStringAsync(ct);
        var nextUrl = ExtractPagerLink(firstHtml);
        using var second = await client.GetAsync(nextUrl, ct);
        var secondHtml = await second.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("Page 00 Subj...", firstHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Page 54 Subj...", firstHtml, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("Page 50 Subj...", secondHtml, StringComparison.Ordinal);
        Assert.Contains("history.back()", secondHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unauthenticated_dead_letters_redirects_to_login()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);

        using var response = await client.GetAsync("/admin/dead-letters", ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/admin/login", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dead_letters_list_shows_empty_state()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var response = await client.GetAsync("/admin/dead-letters", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("DeadLetter はありません", html, StringComparison.Ordinal);
        Assert.DoesNotContain("nav-badge", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dead_letters_list_shows_only_deadlettered_requests()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);
        var deadLetterMailRequestId = Guid.NewGuid();
        var visibleId = await SeedDeadLetterAsync(
            mailRequestId: deadLetterMailRequestId,
            completedAt: now,
            lastErrorMessage: "provider timeout after 30s",
            attemptCount: 3,
            ct);
        var queuedId = await SeedMailRequestAsync(
            sourceService: MailerWebApplicationFixtureBase.SourceService,
            status: MailRequestState.Queued,
            updatedAt: now.AddMinutes(-1),
            recipientEmail: "queued@example.com",
            subject: "Queued Subject",
            ct);
        var failedId = await SeedMailRequestAsync(
            sourceService: MailerWebApplicationFixtureBase.SourceService,
            status: MailRequestState.Failed,
            updatedAt: now.AddMinutes(-2),
            recipientEmail: "failed@example.com",
            subject: "Failed Subject",
            ct);
        var deliveredId = await SeedMailRequestAsync(
            sourceService: MailerWebApplicationFixtureBase.SourceService,
            status: MailRequestState.Delivered,
            updatedAt: now.AddMinutes(-3),
            recipientEmail: "delivered@example.com",
            subject: "Delivered Subject",
            ct);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var response = await client.GetAsync("/admin/dead-letters", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(deadLetterMailRequestId.ToString("D"), html, StringComparison.Ordinal);
        Assert.Contains("provider timeout after 30s", html, StringComparison.Ordinal);
        Assert.Contains("3 / 3", html, StringComparison.Ordinal);
        Assert.Contains(">詳細<", html, StringComparison.Ordinal);
        Assert.Contains("再送する", html, StringComparison.Ordinal);
        Assert.Contains("action-button-disabled", html, StringComparison.Ordinal);
        Assert.Contains("nav-badge", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"1 件\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(queuedId.ToString("D"), html, StringComparison.Ordinal);
        Assert.DoesNotContain(failedId.ToString("D"), html, StringComparison.Ordinal);
        Assert.DoesNotContain(deliveredId.ToString("D"), html, StringComparison.Ordinal);
        Assert.Contains($"/admin/mail-requests/{visibleId:D}", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dead_letters_list_orders_by_completed_at_desc()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);
        var newerMailRequestId = Guid.NewGuid();
        var olderMailRequestId = Guid.NewGuid();
        await SeedDeadLetterAsync(
            mailRequestId: newerMailRequestId,
            completedAt: now,
            lastErrorMessage: "newer",
            attemptCount: 1,
            ct);
        await SeedDeadLetterAsync(
            mailRequestId: olderMailRequestId,
            completedAt: now.AddHours(-1),
            lastErrorMessage: "older",
            attemptCount: 1,
            ct);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var response = await client.GetAsync("/admin/dead-letters", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        var newerIndex = html.IndexOf(newerMailRequestId.ToString("D"), StringComparison.Ordinal);
        var olderIndex = html.IndexOf(olderMailRequestId.ToString("D"), StringComparison.Ordinal);
        Assert.True(newerIndex >= 0);
        Assert.True(olderIndex >= 0);
        Assert.True(newerIndex < olderIndex);
    }

    [Fact]
    public async Task Dead_letters_list_truncates_last_error_message_to_fifty_characters()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);
        var longMessage = new string('x', 60);
        await SeedDeadLetterAsync(
            mailRequestId: Guid.NewGuid(),
            completedAt: now,
            lastErrorMessage: longMessage,
            attemptCount: 1,
            ct);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var response = await client.GetAsync("/admin/dead-letters", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Contains(new string('x', 50) + "...", html, StringComparison.Ordinal);
        Assert.DoesNotContain(longMessage, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mail_requests_list_shows_dead_letter_badge_count()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);
        await SeedDeadLetterAsync(
            mailRequestId: Guid.NewGuid(),
            completedAt: now,
            lastErrorMessage: "one",
            attemptCount: 1,
            ct);
        await SeedDeadLetterAsync(
            mailRequestId: Guid.NewGuid(),
            completedAt: now.AddMinutes(-1),
            lastErrorMessage: "two",
            attemptCount: 1,
            ct);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var response = await client.GetAsync("/admin/mail-requests", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Contains("Dead Letters", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"2 件\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dead_letters_list_pages_with_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 55; i++)
        {
            await SeedDeadLetterAsync(
                mailRequestId: Guid.NewGuid(),
                completedAt: now.AddMinutes(-i),
                lastErrorMessage: $"error-{i:D2}",
                attemptCount: 1,
                ct);
        }

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var first = await client.GetAsync("/admin/dead-letters", ct);
        var firstHtml = await first.Content.ReadAsStringAsync(ct);
        var nextUrl = ExtractPagerLink(firstHtml);
        using var second = await client.GetAsync(nextUrl, ct);
        var secondHtml = await second.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("error-00", firstHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("error-54", firstHtml, StringComparison.Ordinal);
        Assert.StartsWith("/admin/dead-letters?cursor=", nextUrl, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("error-50", secondHtml, StringComparison.Ordinal);
        Assert.Contains("history.back()", secondHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dead_letters_list_rejects_invalid_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var response = await client.GetAsync("/admin/dead-letters?cursor=not-a-valid-cursor", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Dead_letters_list_masks_pii()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);
        await SeedDeadLetterAsync(
            mailRequestId: Guid.NewGuid(),
            completedAt: now,
            lastErrorMessage: "smtp error",
            attemptCount: 1,
            cancellationToken: ct,
            recipientEmail: "user@example.com",
            subject: "Sensitive Subject ABC");

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var response = await client.GetAsync("/admin/dead-letters", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("u***@example.com", html, StringComparison.Ordinal);
        Assert.Contains("Sensitive Su...", html, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Sensitive Subject ABC", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dead_letters_list_excludes_rows_without_completed_at_but_badge_counts_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);
        await SeedDeadLetterAsync(
            mailRequestId: Guid.NewGuid(),
            completedAt: now,
            lastErrorMessage: "with completed_at",
            attemptCount: 1,
            ct);
        await SeedDeadLetterWithoutCompletedAtAsync(ct);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var listResponse = await client.GetAsync("/admin/dead-letters", ct);
        var listHtml = await listResponse.Content.ReadAsStringAsync(ct);
        using var mailRequestsResponse = await client.GetAsync("/admin/mail-requests", ct);
        var mailRequestsHtml = await mailRequestsResponse.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Contains("with completed_at", listHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("missing completed_at", listHtml, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"2 件\"", mailRequestsHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dead_letters_list_pages_through_completed_rows_when_null_row_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 51; i++)
        {
            await SeedDeadLetterAsync(
                mailRequestId: Guid.NewGuid(),
                completedAt: now.AddMinutes(-i),
                lastErrorMessage: $"error-{i:D2}",
                attemptCount: 1,
                ct);
        }

        await SeedDeadLetterWithoutCompletedAtAsync(ct);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var first = await client.GetAsync("/admin/dead-letters", ct);
        var firstHtml = await first.Content.ReadAsStringAsync(ct);
        var nextUrl = ExtractPagerLink(firstHtml);
        using var second = await client.GetAsync(nextUrl, ct);
        var secondHtml = await second.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("error-00", firstHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("error-50", firstHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("missing completed_at", firstHtml, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("error-50", secondHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("missing completed_at", secondHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dead_letters_detail_link_renders_detail_page()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);
        var mailRequestId = Guid.NewGuid();
        var internalId = await SeedDeadLetterAsync(
            mailRequestId: mailRequestId,
            completedAt: now,
            lastErrorMessage: "detail-link-test",
            attemptCount: 3,
            ct);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var detail = await client.GetAsync($"/admin/mail-requests/{internalId:D}", ct);
        var html = await detail.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Contains(mailRequestId.ToString("D"), html, StringComparison.Ordinal);
        Assert.Contains("DeadLettered", html, StringComparison.Ordinal);
        Assert.Contains("detail-link-test", html, StringComparison.Ordinal);
    }

    private static HttpClient CreateClient(WebApplicationFactory<global::Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private static async Task LoginAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var csrfToken = await ReadCsrfTokenAsync(client, cancellationToken);
        using var response = await client.PostAsync(
            "/admin/api/login",
            CreateLoginContent(csrfToken, MailerAdminFixture.Username, MailerAdminFixture.Password),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<string> ReadCsrfTokenAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("/admin/login", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        const string marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Login page did not contain a CSRF token.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end > start, "Login page CSRF token value was empty.");
        return html[start..end];
    }

    private static FormUrlEncodedContent CreateLoginContent(
        string csrfToken,
        string username,
        string password) =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrfToken,
            ["username"] = username,
            ["password"] = password,
        });

    private static MailerAdminOptions LoadAdminOptions(IReadOnlyDictionary<string, string?> settings) =>
        MailerAdminOptions.Load(
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings)
                .Build());

    private async Task<Guid> SeedMailRequestAsync(
        string sourceService,
        MailRequestState status,
        DateTimeOffset updatedAt,
        string recipientEmail,
        string subject,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts,
                accepted_at, created_at, updated_at, completed_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, 'AdminListTest',
                '{}', @PayloadHash, @Subject, @RecipientEmail,
                @Status, 0, 3,
                @AcceptedAt, @CreatedAt, @UpdatedAt, @CompletedAt);
            """;
        command.Parameters.AddWithValue("@Id", id.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", MailerWebApplicationFixtureBase.TenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", sourceService);
        command.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('0', 64));
        command.Parameters.AddWithValue("@Subject", subject);
        command.Parameters.AddWithValue("@RecipientEmail", recipientEmail);
        command.Parameters.AddWithValue("@Status", (int)status);
        command.Parameters.AddWithValue("@AcceptedAt", SqliteTime.ToStorageUtc(updatedAt));
        command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(updatedAt));
        command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(updatedAt));
        command.Parameters.AddWithValue(
            "@CompletedAt",
            status is MailRequestState.Delivered or MailRequestState.Failed or MailRequestState.DeadLettered
                ? SqliteTime.ToStorageUtc(updatedAt)
                : DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    private async Task<Guid> SeedDeadLetterAsync(
        Guid mailRequestId,
        DateTimeOffset completedAt,
        string lastErrorMessage,
        int attemptCount,
        CancellationToken cancellationToken,
        string recipientEmail = "dead@example.com",
        string subject = "Dead Letter Subject")
    {
        var id = Guid.NewGuid();
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts, last_error_message,
                accepted_at, created_at, updated_at, completed_at, failed_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, 'AdminDeadLetterTest',
                '{}', @PayloadHash, @Subject, @RecipientEmail,
                @Status, @AttemptCount, 3, @LastErrorMessage,
                @AcceptedAt, @CreatedAt, @UpdatedAt, @CompletedAt, @FailedAt);
            """;
        command.Parameters.AddWithValue("@Id", id.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", MailerWebApplicationFixtureBase.TenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", MailerWebApplicationFixtureBase.SourceService);
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('0', 64));
        command.Parameters.AddWithValue("@Subject", subject);
        command.Parameters.AddWithValue("@RecipientEmail", recipientEmail);
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.DeadLettered);
        command.Parameters.AddWithValue("@AttemptCount", attemptCount);
        command.Parameters.AddWithValue("@LastErrorMessage", lastErrorMessage);
        command.Parameters.AddWithValue("@AcceptedAt", SqliteTime.ToStorageUtc(completedAt));
        command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(completedAt));
        command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(completedAt));
        command.Parameters.AddWithValue("@CompletedAt", SqliteTime.ToStorageUtc(completedAt));
        command.Parameters.AddWithValue("@FailedAt", SqliteTime.ToStorageUtc(completedAt));

        await command.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    private async Task<Guid> SeedDeadLetterWithoutCompletedAtAsync(CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 6, 24, 11, 0, 0, TimeSpan.Zero);
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts, last_error_message,
                accepted_at, created_at, updated_at, completed_at, failed_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, 'AdminDeadLetterTest',
                '{}', @PayloadHash, @Subject, @RecipientEmail,
                @Status, @AttemptCount, 3, @LastErrorMessage,
                @AcceptedAt, @CreatedAt, @UpdatedAt, NULL, NULL);
            """;
        command.Parameters.AddWithValue("@Id", id.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", MailerWebApplicationFixtureBase.TenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", MailerWebApplicationFixtureBase.SourceService);
        command.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('0', 64));
        command.Parameters.AddWithValue("@Subject", "Dead Letter Subject");
        command.Parameters.AddWithValue("@RecipientEmail", "dead@example.com");
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.DeadLettered);
        command.Parameters.AddWithValue("@AttemptCount", 1);
        command.Parameters.AddWithValue("@LastErrorMessage", "missing completed_at");
        command.Parameters.AddWithValue("@AcceptedAt", SqliteTime.ToStorageUtc(now));
        command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(now));
        command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(now));

        await command.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    private static string ExtractPagerLink(string html)
    {
        const string marker = "class=\"pager-link\" href=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Pager link was not rendered.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end > start, "Pager link href was empty.");
        return WebUtility.HtmlDecode(html[start..end]);
    }
}
