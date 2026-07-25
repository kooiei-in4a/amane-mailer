using Amane.Mailer.Configuration;

namespace Amane.Mailer.Tests.Webhooks;

/// <summary>
/// Source-level guards for #353: ReconcileBatchSize names reconcile search size only;
/// webhook delivery remains single-claim sequential.
/// </summary>
public sealed class WebhookReconcileBatchSizeSourceTests
{
    [Fact]
    public void Sweep_service_passes_reconcile_batch_size_to_enqueuer()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Amane.Mailer",
            "Worker",
            "WebhookDeliverySweepService.cs"));

        Assert.Contains("ReconcileMissingTerminalEventsAsync", source, StringComparison.Ordinal);
        Assert.Contains("webhookOptions.ReconcileBatchSize", source, StringComparison.Ordinal);
        Assert.DoesNotContain("webhookOptions.BatchClaimSize", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Delivery_worker_claims_one_at_a_time_and_ignores_reconcile_batch_size()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Amane.Mailer",
            "Webhooks",
            "WebhookDeliveryWorker.cs"));

        Assert.Contains("TryClaimOneAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReconcileBatchSize", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BatchClaimSize", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxDeliveryConcurrency", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxSendConcurrency", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_type_exposes_reconcile_batch_size_not_delivery_claim_size()
    {
        Assert.NotNull(typeof(MailerWebhookOptions).GetProperty(nameof(MailerWebhookOptions.ReconcileBatchSize)));
        Assert.Null(typeof(MailerWebhookOptions).GetProperty("BatchClaimSize"));
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Amane.Mailer.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Could not locate repository root from test base directory.");
    }
}
