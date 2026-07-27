namespace Amane.Mailer.Operations.EventGridConfigCheck;

public interface IAzureCliRunner
{
    Task<AzureCliRunResult> RunAsync(AzureCliQuery query, CancellationToken cancellationToken);
}
