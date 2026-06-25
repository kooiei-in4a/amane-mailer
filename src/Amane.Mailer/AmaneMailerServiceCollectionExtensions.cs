using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Delivery;
using Amane.Mailer.Operations;
using Amane.Mailer.Queue;
using Amane.Mailer.Worker;

namespace Amane.Mailer;

public static class AmaneMailerServiceCollectionExtensions
{
    public static IServiceCollection AddAmaneMailerServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<MailDeliveryInflightTracker>();
        services.AddMailerAdmin(configuration);

        services.AddSingleton(provider =>
            MailerTenantRegistry.Load(provider.GetRequiredService<IConfiguration>()));

        services.AddSingleton(provider =>
            MailerOptions.Load(provider.GetRequiredService<IConfiguration>()));

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
            .Configure<MailerWorkerOptions>((options, workerOptions) =>
            {
                options.ShutdownTimeout = workerOptions.HostShutdownTimeout;
            });

        services.AddSingleton(provider =>
            MailerSweepOptions.Load(provider.GetRequiredService<IConfiguration>()));

        services.AddSingleton(provider =>
            MailerRetentionOptions.Load(provider.GetRequiredService<IConfiguration>()));

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

        services.AddSingleton<SqliteConnectionFactory>();

        services.AddSingleton<MailRequestRepository>();
        services.AddSingleton<ExpiredProcessingReaper>();

        services.AddSingleton<SqlMigrationRunner>();

        services.AddSingleton<MailRequestQueue>();

        services.AddSingleton<IMailRequestQueue>(provider => provider.GetRequiredService<MailRequestQueue>());

        services.AddSingleton<IMailDeliveryProvider, MailDeliveryProviderRouter>();

        services.AddSingleton<MailpitMailDeliveryProvider>();

        services.AddSingleton<AcsMailDeliveryProvider>();

        services.AddScoped<DbMigrateCommand>();

        if (configuration.GetValue("Mailer:Worker:Enabled", true))
        {
            services.AddHostedService<MailRequestSweepService>();
            services.AddHostedService<RetentionService>();
            services.AddHostedService<MailerWalCheckpointShutdownService>();
            services.AddHostedService<MailRequestWorker>();
        }

        return services;
    }
}
