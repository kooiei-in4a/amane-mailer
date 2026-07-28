namespace Amane.Mailer.Setup;

/// <summary>
/// Three-state symlink/reparse inspection. Inspection failure must be treated as unsafe.
/// </summary>
public enum SetupLinkInspectionResult
{
    NotALink = 0,
    IsLinkOrReparse = 1,
    InspectionFailed = 2,
}
