namespace Amane.Mailer.Setup;

/// <summary>
/// ADR 0021 D-04 canonical integrity merge. Both host at-rest and mount attestation must match.
/// </summary>
public static class SetupIntegrityMerger
{
    public const string Matched = "matched";
    public const string Mismatch = "mismatch";
    public const string NotVerified = "not-verified";
    public const string NotManaged = "not-managed";

    public static string Merge(string hostAtRest, string mountAttestation)
    {
        if (string.Equals(hostAtRest, NotManaged, StringComparison.Ordinal)
            || string.Equals(mountAttestation, NotManaged, StringComparison.Ordinal))
        {
            return NotManaged;
        }

        if (string.Equals(hostAtRest, Mismatch, StringComparison.Ordinal)
            || string.Equals(mountAttestation, Mismatch, StringComparison.Ordinal))
        {
            return Mismatch;
        }

        if (string.Equals(hostAtRest, Matched, StringComparison.Ordinal)
            && string.Equals(mountAttestation, Matched, StringComparison.Ordinal))
        {
            return Matched;
        }

        return NotVerified;
    }
}
