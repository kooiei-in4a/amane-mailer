using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Amane.Mailer.Configuration;

/// <summary>
/// DI helpers that keep startup validation inventory in sync with singleton registration.
/// When adding a new startup-required options type:
/// 1. Implement <c>Load</c> / <c>Validate</c> on the options type (preserve Worker/Admin enabled gates).
/// 2. Register it with <see cref="AddStartupValidatedSingleton{TService}"/> (not plain <c>AddSingleton</c>).
/// 3. Extend <c>MailerStartupValidationInventoryTests.ExpectedStartupValidatedTypes</c>.
/// 4. Add a focused host-startup failure test for the new invariant.
/// Do not enumerate options types in <c>Program.cs</c>.
/// </summary>
public static class MailerStartupValidationServiceCollectionExtensions
{
    public static IServiceCollection AddMailerStartupValidator(this IServiceCollection services)
    {
        GetOrAddCatalog(services);
        services.TryAddSingleton<MailerStartupValidator>();
        return services;
    }

    /// <summary>
    /// Registers <typeparamref name="TService"/> as a singleton and adds it to the startup
    /// validation catalog so host startup resolves it eagerly.
    /// </summary>
    public static IServiceCollection AddStartupValidatedSingleton<TService>(
        this IServiceCollection services,
        Func<IServiceProvider, TService> implementationFactory)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(implementationFactory);
        GetOrAddCatalog(services).Register<TService>();
        services.AddSingleton(implementationFactory);
        return services;
    }

    internal static MailerStartupValidationCatalog GetOrAddCatalog(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(MailerStartupValidationCatalog)
                && descriptor.ImplementationInstance is MailerStartupValidationCatalog existing)
            {
                return existing;
            }
        }

        var catalog = new MailerStartupValidationCatalog();
        services.AddSingleton(catalog);
        return catalog;
    }
}
