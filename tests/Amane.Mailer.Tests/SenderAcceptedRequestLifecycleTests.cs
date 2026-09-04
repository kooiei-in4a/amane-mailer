using System.Net;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Data;
using Amane.Mailer.Identity;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

[Collection(MailerTestCollection.Name)]
public sealed class SenderAcceptedRequestLifecycleTests(MailerWorkerFixture fixture)
    : IClassFixture<MailerWorkerFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        fixture.DeliveryProvider.Reset();
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SenderRepository>()
            .EnableAsync(MailerWebApplicationFixtureBase.TenantId, TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        fixture.DeliveryProvider.ReleaseHeldSend();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Accepted_delivery_continues_after_accepting_key_revoke_and_sender_disable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var senders = scope.ServiceProvider.GetRequiredService<SenderRepository>();
        var key = await senders.CreateApiKeyAsync(MailerWebApplicationFixtureBase.TenantId, "lifecycle", ct);
        fixture.DeliveryProvider.HoldNextSendIgnoringCancellation();

        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", key.Plaintext);
        var request = MailRequestTestData.CreateRequest();
        using var response = await client.PostAsync(
            "/api/mail-requests", MailRequestTestData.ToJsonContent(request), ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await fixture.DeliveryProvider.WaitUntilHoldConsumedAsync(ct);

        await senders.RevokeApiKeyAsync(key.KeyId, ct);
        await senders.DisableAsync(MailerWebApplicationFixtureBase.TenantId, ct);
        Assert.NotNull(await senders.FindAsync(MailerWebApplicationFixtureBase.TenantId, ct));
        fixture.DeliveryProvider.ReleaseHeldSend();

        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        MailRequestIdempotencyRow? stored = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            stored = await repository.FindByIdempotencyKeyAsync(
                MailerWebApplicationFixtureBase.TenantId,
                V2PersistenceCompatibility.SourceService,
                request.MailRequestId,
                ct);
            if (stored?.Status == MailRequestState.Delivered)
            {
                break;
            }
            await Task.Delay(25, ct);
        }

        Assert.NotNull(stored);
        Assert.Equal(MailRequestState.Delivered, stored.Status);
        Assert.Single(fixture.DeliveryProvider.Sent);
        await senders.EnableAsync(MailerWebApplicationFixtureBase.TenantId, ct);
    }

    [Fact]
    public async Task Suppression_is_instance_wide_across_senders()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var senders = scope.ServiceProvider.GetRequiredService<SenderRepository>();
        var other = await senders.CreateAsync($"suppression-{Guid.NewGuid():N}@example.com", "Other", ct);
        var key = await senders.CreateApiKeyAsync(other.SenderId, "suppression", ct);
        var suppressions = scope.ServiceProvider.GetRequiredService<MailSuppressionRepository>();
        await suppressions.TryInsertAsync(new MailSuppressionInsert
        {
            Id = Guid.NewGuid(),
            TenantId = V2PersistenceCompatibility.SuppressionScopeId,
            RecipientEmail = "recipient@example.com",
            Reason = MailSuppressionReasons.Manual,
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct);

        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", key.Plaintext);
        var request = MailRequestTestData.CreateRequest();
        using var response = await client.PostAsync(
            "/api/mail-requests", MailRequestTestData.ToJsonContent(request), ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        MailRequestIdempotencyRow? stored = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            stored = await repository.FindByIdempotencyKeyAsync(
                other.SenderId,
                V2PersistenceCompatibility.SourceService,
                request.MailRequestId,
                ct);
            if (stored?.Status == MailRequestState.Failed)
            {
                break;
            }
            await Task.Delay(25, ct);
        }

        Assert.NotNull(stored);
        Assert.Equal(MailRequestState.Failed, stored.Status);
        Assert.Empty(fixture.DeliveryProvider.Sent);
    }
}
