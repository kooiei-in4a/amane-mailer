namespace Amane.Mailer.Operations.AcsSetup;

/// <summary>
/// Minimal masking for confirmation UI. Never log the unmasked value.
/// </summary>
public static class AcsAddressMask
{
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrEmpty(email))
        {
            return "***";
        }

        var at = email.IndexOf('@');
        if (at <= 0 || at == email.Length - 1)
        {
            return "***";
        }

        var local = email[..at];
        var domain = email[(at + 1)..];
        var localVisible = local.Length == 1 ? "*" : local[0] + "***";
        return localVisible + "@" + domain;
    }
}
