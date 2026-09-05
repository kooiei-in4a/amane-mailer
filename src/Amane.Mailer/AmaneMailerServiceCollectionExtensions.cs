using Amane.Mailer.Admin;
using Amane.Mailer.Attachments.Spool;
using Amane.Mailer.Bounce;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Delivery;
using Amane.Mailer.Identity;
using Amane.Mailer.Operations;
using Amane.Mailer.Queue;
using Amane.Mailer.Setup;
using Amane.Mailer.Webhooks;
using Amane.Mailer.Worker;

namespace Amane.Mailer;

public static class AmaneMailerServiceCollectionExtensions
{
    public static IServiceCollection AddAmaneMailerServices(
        this IServiceCollection services,
        IConfiguration configuration,
        InstanceRuntimeState? instanceState = null)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddMailerStartupValidator();
        services.AddSingleton(instanceState ?? InstanceRuntimeState.Unknown);
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<SqlMigrationRunner>();

        if (instanceState?.IsUninitialized == true)
        {
            FirstRunSetupEndpoints.AddServices(services);
            return services;
        }

        services.AddMailerAdmin(configuration, instanceState);

        services.AddStartupValidatedSingleton(provider =>
            MailerConfigurationSnapshot.Load(
                provider.GetRequiredService<IConfiguration>(),
                provider.GetRequiredService<IHostEnvironment>().EnvironmentName,
                provider.GetRequiredService<InstanceRuntimeState>()));

        services.AddStartupValidatedSingleton(provider =>
            provider.GetRequiredService<MailerConfigurationSnapshot>().Registry);

        services.AddStartupValidatedSingleton(provider =>
            provider.GetRequiredService<MailerConfigurationSnapshot>().Options);

        services.AddStartupValidatedSingleton(provider =>
        {
            var resolvedConfiguration = provider.GetRequiredService<IConfiguration>();
            var options = MailerWorkerOptions.Load(resolvedConfiguration);
            if (MailerWorkerOptions.IsEnabled(resolvedConfiguration))
            {
                options.Validate();
            }

            return options;
        });

        services.AddOptions<HostOptions>()
            .Configure<MailerWorkerOptions>((options, workerOptions) =>
                options.ShutdownTimeout = workerOptions.HostShutdownTimeout);

        services.AddStartupValidatedSingleton(provider =>
            MailerSweepOptions.Load(provider.GetRequiredService<IConfiguration>()));

        services.AddStartupValidatedSingleton(provider =>
            MailerRetentionOptions.Load(provider.GetRequiredService<IConfiguration>()));

        services.AddStartupValidatedSingleton(provider =>
        {
            var resolvedConfiguration = provider.GetRequiredService<IConfiguration>();
            var options = MailerAdminAuditRetentionOptions.Load(resolvedConfiguration);
            if (MailerWorkerOptions.IsEnabled(resolvedConfiguration))
            {
                options.Validate();
            }

            return options;
        });

        services.AddSingleton<WorkerServiceStatus>();

        services.AddStartupValidatedSingleton(provider =>
        {
            var resolvedConfiguration = provider.GetRequiredService<IConfiguration>();
            var options = MailerHealthcheckOptions.Load(resolvedConfiguration);
            if (MailerWorkerOptions.IsEnabled(resolvedConfiguration))
            {
                var workerOptions = provider.GetRequiredService<MailerWorkerOptions>();
                var sweepOptions = provider.GetRequiredService<MailerSweepOptions>();
                options.Validate(workerOptions, sweepOptions);
            }

            return options;
        });

        services.AddStartupValidatedSingleton(provider =>
        {
            var options = MailerMetricsOptions.Load(provider.GetRequiredService<IConfiguration>());
            options.Validate(provider.GetRequiredService<IHostEnvironment>().EnvironmentName);
            return options;
        });

        services.AddStartupValidatedSingleton(provider =>
        {
            var options = MailerBounceIngestionOptions.Load(provider.GetRequiredService<IConfiguration>());
            options.Validate();
            return options;
        });

        services.AddSingleton<MailerRuntimeMetrics>();
        services.AddSingleton<MailerReadinessEvaluator>();

        services.AddSingleton<SenderRepository>();
        services.AddSingleton<SenderDeliveryConfigurationAdapter>();
        services.AddSingleton<ApiAuthenticationRateLimiter>();
        services.AddSingleton<MailerDbStatsReader>();
        services.AddSingleton<MailerDbStorageInfoReader>();

        services.AddSingleton(provider => AttachmentSpoolOptions.Resolve(
            provider.GetRequiredService<IConfiguration>(),
            provider.GetRequiredService<SqliteConnectionFactory>()));
        services.AddSingleton(provider =>
        {
            var spool = new AttachmentSpool(provider.GetRequiredService<AttachmentSpoolOptions>());
            spool.EnsureRootDirectoriesExist();
            return spool;
        });
        services.AddSingleton<MailRequestAttachmentStore>();
        services.AddSingleton<MailAttachmentSubmissionStore>();
        services.AddSingleton<MailerMaintenanceLeaseStore>();

        services.AddSingleton<MailRequestClaimStore>();
        services.AddSingleton<MailRequestAcceptStore>();
        services.AddSingleton<MailRequestConsumerMutations>();
        services.AddSingleton<MailRequestAdminQueries>();
        services.AddSingleton<WorkerHeartbeatStore>();
        services.AddSingleton<MailRequestRecipientStore>();
        services.AddSingleton<MailPlainSubmissionStore>();
        services.AddSingleton<MailRequestRepository>();
        services.AddSingleton<AdminAuditRepository>();
        services.AddSingleton<ProviderEventInboxRepository>();
        services.AddSingleton<ProviderQueueDeadLetterRepository>();
        services.AddSingleton<BounceEventRepository>();
        services.AddSingleton<MailSuppressionRepository>();
        services.AddSingleton<BounceIngestionStore>();
        services.AddSingleton<BounceIngestionQueue>();
        services.AddSingleton<IBounceIngestionQueue>(provider => provider.GetRequiredService<BounceIngestionQueue>());
        services.AddSingleton<DeliveryEventRepository>();
        services.AddSingleton<ExpiredProcessingReaper>();
        services.AddSingleton<DeliveryEventEnqueuer>();

        services.AddSingleton<MailRequestQueue>();

        services.AddSingleton<IMailRequestQueue>(provider => provider.GetRequiredService<MailRequestQueue>());

        services.AddSingleton<IMailDeliveryProvider, MailDeliveryProviderRouter>();

        services.AddSingleton<MailpitMailDeliveryProvider>();

        services.AddSingleton<AcsMailDeliveryProvider>();

        services.AddScoped<DbMigrateCommand>();

        if (MailerWorkerOptions.IsEnabled(configuration))
        {
            services.AddSingleton<MailRequestDispatcher>();
            services.AddHostedService<MailRequestSweepService>();
            services.AddHostedService<RetentionService>();
            services.AddHostedService<AdminAuditRetentionService>();
            services.AddHostedService<MailerWalCheckpointShutdownService>();
            services.AddHostedService<MailRequestWorker>();
            services.AddHostedService<AttachmentSpoolReconciliationService>();

            if (MailerBounceIngestionOptions.IsEnabled(configuration))
            {
                services.AddHostedService<BounceIngestionSweepService>();
                services.AddHostedService<BounceIngestionWorker>();
            }

            if (MailerBounceIngestionOptions.IsQueuePollingConfigured(configuration))
            {
                services.AddSingleton<IAcsEventQueueClient, AzureAcsEventQueueClient>();
                services.AddHostedService<AcsQueuePollingService>();
            }
        }

        return services;
    }
}
