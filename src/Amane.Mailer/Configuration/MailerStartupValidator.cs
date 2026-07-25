namespace Amane.Mailer.Configuration;

/// <summary>
/// Resolves every service registered in <see cref="MailerStartupValidationCatalog"/> so
/// configuration <c>Load</c> / <c>Validate</c> runs on a single startup path.
/// </summary>
public sealed class MailerStartupValidator
{
    private readonly IServiceProvider _services;
    private readonly MailerStartupValidationCatalog _catalog;

    public MailerStartupValidator(
        IServiceProvider services,
        MailerStartupValidationCatalog catalog)
    {
        _services = services;
        _catalog = catalog;
    }

    public void Validate() => _catalog.ResolveAll(_services);
}
