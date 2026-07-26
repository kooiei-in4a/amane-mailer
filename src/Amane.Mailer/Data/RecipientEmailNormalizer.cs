namespace Amane.Mailer.Data;

/// <summary>
/// Canonical recipient normalization for <c>mail_suppressions</c> storage and lookup.
/// Store (#301), send-time check (#303), and removal CLI (#400) must all call this.
/// </summary>
public static class RecipientEmailNormalizer
{
    /// <summary>
    /// Trims surrounding whitespace and lowercases the full address (local-part and domain)
    /// with <see cref="string.ToLowerInvariant"/>. Does not use SQLite <c>NOCASE</c> collation.
    /// </summary>
    /// <param name="email">Non-empty recipient address.</param>
    /// <returns>Normalized address suitable for UNIQUE (tenant_id, recipient_email).</returns>
    public static string Normalize(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return email.Trim().ToLowerInvariant();
    }
}
