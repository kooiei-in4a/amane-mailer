using Amane.Mailer.Json;

namespace Amane.Mailer;

public static class MailerJsonConfiguration
{
    public static IServiceCollection AddMailerJsonSerialization(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, MailerJsonContext.Default);
        });

        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, MailerJsonContext.Default);
        });

        return services;
    }
}
