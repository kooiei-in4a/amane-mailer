using Amane.Mailer.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailerWorkerOptionsTests
{
    [Fact]
    public void Default_worker_options_are_lease_safe()
    {
        var options = new MailerWorkerOptions();

        options.Validate();
    }

    [Fact]
    public void Validate_rejects_batch_waves_that_outlive_the_lease()
    {
        var options = new MailerWorkerOptions
        {
            BatchClaimSize = 10,
            MaxSendConcurrency = 4,
            SendTimeoutSeconds = 90,
            LeaseDurationSeconds = 120,
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void ShutdownDrainTimeout_covers_one_in_flight_wave_not_full_batch()
    {
        // Multi-wave batches cancel semaphore waiters on shutdown (#271);
        // drain budget stays one SendTimeout + FinalizeTimeout window.
        var options = new MailerWorkerOptions
        {
            BatchClaimSize = 8,
            MaxSendConcurrency = 2,
            SendTimeoutSeconds = 90,
            LeaseDurationSeconds = 400,
        };

        options.Validate();
        Assert.Equal(
            TimeSpan.FromSeconds(90 + MailerWorkerOptions.FinalizeTimeoutSeconds),
            options.ShutdownDrainTimeout);
    }
}
