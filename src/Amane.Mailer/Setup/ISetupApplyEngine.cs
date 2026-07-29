namespace Amane.Mailer.Setup;

/// <summary>
/// Apply / recover surface used by Easy Setup ACS workflow (#451) without depending on Docker
/// construction details in unit tests.
/// </summary>
public interface ISetupApplyEngine
{
    Task<SetupApplyResult> ApplyAsync(
        TrustedSetupHostLayout layout,
        string candidateBundleId,
        CancellationToken cancellationToken);

    Task<SetupApplyResult> RecoverAsync(
        TrustedSetupHostLayout layout,
        CancellationToken cancellationToken);
}
