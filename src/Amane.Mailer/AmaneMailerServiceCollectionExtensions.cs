using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Delivery;
using Amane.Mailer.Operations;
using Amane.Mailer.Queue;
using Amane.Mailer.Webhooks;
using Amane.Mailer.Worker;

namespace Amane.Mailer;

public static class AmaneMailerServiceCollectionExtensions
{
    public static IServiceCollection AddAmaneMailerServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddMailerStartupValidator();
        services.AddMailerAdmin(configuration);

        services.AddStartupValidatedSingleton(provider =>
            MailerTenantRegistry.Load(provider.GetRequiredService<IConfiguration>()));

        services.AddStartupValidatedSingleton(provider =>
        {
            var options = MailerOptions.Load(provider.GetRequiredService<IConfiguration>());
            var tenants = provider.GetRequiredService<MailerTenantRegistry>();
            options.ValidateEffectiveProviders(tenants.ListTenants());
            return options;
        });

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
            .Configure<MailerWorkerOptions, MailerWebhookOptions>((options, workerOptions, webhookOptions) =>
            {
                var mailHostTimeout = workerOptions.HostShutdownTimeout;
                var webhookHostTimeout = webhookOptions.HostShutdownTimeout;
                options.ShutdownTimeout = mailHostTimeout > webhookHostTimeout
                    ? mailHostTimeout
                    : webhookHostTimeout;
            });

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

        services.AddStartupValidatedSingleton(provider =>
        {
            var resolvedConfiguration = provider.GetRequiredService<IConfiguration>();
            var logger = provider.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(MailerWebhookOptions));
            var options = MailerWebhookOptions.Load(resolvedConfiguration, logger);
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

        services.AddSingleton<MailerRuntimeMetrics>();
        services.AddSingleton<MailerReadinessEvaluator>();

        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<MailerDbStatsReader>();
        services.AddSingleton<MailerDbStorageInfoReader>();

        services.AddSingleton<MailRequestClaimStore>();
        services.AddSingleton<MailRequestAcceptStore>();
        services.AddSingleton<MailRequestConsumerMutations>();
        services.AddSingleton<MailRequestAdminQueries>();
        services.AddSingleton<WorkerHeartbeatStore>();
        services.AddSingleton<MailRequestRepository>();
        services.AddSingleton<AdminAuditRepository>();
        services.AddSingleton<DeliveryEventRepository>();
        services.AddSingleton<ExpiredProcessingReaper>();
        services.AddSingleton<WebhookUrlValidator>();
        services.AddSingleton<WebhookSignatureService>();
        services.AddSingleton<WebhookDeliveryClient>();
        services.AddSingleton<DeliveryEventEnqueuer>();
        services.AddSingleton<WebhookDeliveryQueue>();
        services.AddSingleton<IWebhookDeliveryQueue>(provider => provider.GetRequiredService<WebhookDeliveryQueue>());

        services.AddSingleton<SqlMigrationRunner>();

        services.AddSingleton<MailRequestQueue>();

        services.AddSingleton<IMailRequestQueue>(provider => provider.GetRequiredService<MailRequestQueue>());

        services.AddSingleton<IMailDeliveryProvider, MailDeliveryProviderRouter>();

        services.AddSingleton<MailpitMailDeliveryProvider>();

        services.AddSingleton<AcsMailDeliveryProvider>();

        services.AddScoped<DbMigrateCommand>();

        // WebhookDeliveryClient is always registered; keep IHttpClientFactory + named client
        // available even when Mailer:Worker:Enabled=false so Development ValidateOnBuild
        // (and worker-disabled hosts) can construct the graph (#341 AOT path smoke).
        services.AddWebhookHttpClient();

        if (MailerWorkerOptions.IsEnabled(configuration))
        {
            services.AddHostedService<MailRequestSweepService>();
            services.AddHostedService<WebhookDeliverySweepService>();
            services.AddHostedService<RetentionService>();
            services.AddHostedService<AdminAuditRetentionService>();
            services.AddHostedService<MailerWalCheckpointShutdownService>();
            services.AddHostedService<MailRequestWorker>();
            services.AddHostedService<WebhookDeliveryWorker>();
        }

        return services;
    }
}
