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
        services.AddMailerAdmin(configuration);

        services.AddSingleton(provider =>
            MailerTenantRegistry.Load(provider.GetRequiredService<IConfiguration>()));

        services.AddSingleton(provider =>
        {
            var options = MailerOptions.Load(provider.GetRequiredService<IConfiguration>());
            var tenants = provider.GetRequiredService<MailerTenantRegistry>();
            options.ValidateEffectiveProviders(tenants.ListTenants());
            return options;
        });

        services.AddSingleton(provider =>
        {
            var resolvedConfiguration = provider.GetRequiredService<IConfiguration>();
            var options = MailerWorkerOptions.Load(resolvedConfiguration);
            if (resolvedConfiguration.GetValue("Mailer:Worker:Enabled", true))
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

        services.AddSingleton(provider =>
            MailerSweepOptions.Load(provider.GetRequiredService<IConfiguration>()));

        services.AddSingleton(provider =>
            MailerRetentionOptions.Load(provider.GetRequiredService<IConfiguration>()));

        services.AddSingleton(provider =>
        {
            var resolvedConfiguration = provider.GetRequiredService<IConfiguration>();
            var options = MailerAdminAuditRetentionOptions.Load(resolvedConfiguration);
            if (resolvedConfiguration.GetValue("Mailer:Worker:Enabled", true))
            {
                options.Validate();
            }

            return options;
        });

        services.AddSingleton(provider =>
        {
            var resolvedConfiguration = provider.GetRequiredService<IConfiguration>();
            var options = MailerWebhookOptions.Load(resolvedConfiguration);
            if (resolvedConfiguration.GetValue("Mailer:Worker:Enabled", true))
            {
                options.Validate();
            }

            return options;
        });

        services.AddSingleton<WorkerServiceStatus>();

        services.AddSingleton(provider =>
        {
            var resolvedConfiguration = provider.GetRequiredService<IConfiguration>();
            var options = MailerHealthcheckOptions.Load(resolvedConfiguration);
            if (resolvedConfiguration.GetValue("Mailer:Worker:Enabled", true))
            {
                var workerOptions = provider.GetRequiredService<MailerWorkerOptions>();
                var sweepOptions = provider.GetRequiredService<MailerSweepOptions>();
                options.Validate(workerOptions, sweepOptions);
            }

            return options;
        });

        services.AddSingleton(provider =>
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

        if (configuration.GetValue("Mailer:Worker:Enabled", true))
        {
            var webhookOptions = MailerWebhookOptions.Load(configuration);
            services.AddWebhookHttpClient(webhookOptions);
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
