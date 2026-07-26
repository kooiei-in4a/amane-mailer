namespace Amane.Mailer.Admin;

/// <summary>
/// Named Admin capability boundaries (ADR 0013 D-07).
/// MVP may grant all capabilities to a single authenticated admin role, but call sites
/// must still check by capability name so later role splits stay localized.
/// </summary>
public static class AdminCapabilities
{
    public const string ViewUnmaskedListPii = "view_unmasked_list_pii";

    /// <summary>
    /// Whether the current Admin configuration grants the named capability.
    /// </summary>
    public static bool Has(MailerAdminOptions options, string capability)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);

        return capability switch
        {
            // Suppressions / list unmask: PII_LIST_MODE=visible only (not MASK_RECIPIENTS=false).
            ViewUnmaskedListPii => options.ListPiiVisible,
            _ => false,
        };
    }
}