namespace Amane.Mailer.Configuration;

/// <summary>
/// Inventory of configuration services that must be resolved during host startup
/// so <c>Load</c> / <c>Validate</c> fail-fast before accepting traffic.
/// Entries are registered via <see cref="MailerStartupValidationServiceCollectionExtensions.AddStartupValidatedSingleton{TService}"/>.
/// </summary>
public sealed class MailerStartupValidationCatalog
{
    private readonly List<Type> _serviceTypes = [];
    private readonly List<Action<IServiceProvider>> _resolvers = [];

    public IReadOnlyList<Type> ServiceTypes => _serviceTypes;

    internal void Register<TService>()
        where TService : class
    {
        var serviceType = typeof(TService);
        if (_serviceTypes.Contains(serviceType))
            return;

        _serviceTypes.Add(serviceType);
        _resolvers.Add(static services => _ = services.GetRequiredService<TService>());
    }

    internal void ResolveAll(IServiceProvider services)
    {
        foreach (var resolve in _resolvers)
        {
            resolve(services);
        }
    }
}
