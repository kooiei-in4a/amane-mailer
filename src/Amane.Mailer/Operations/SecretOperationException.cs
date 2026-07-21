namespace Amane.Mailer.Operations;

/// <summary>
/// Carries a canonical, sanitized result code for secret-registration failures. Callers must
/// surface <see cref="CanonicalCode"/> only; <see cref="Exception.Message"/> and
/// <see cref="Exception.InnerException"/> may reference paths but must never contain secret
/// values and must not be echoed to stdout/stderr/logs.
/// </summary>
public sealed class SecretOperationException(string canonicalCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string CanonicalCode { get; } = canonicalCode;
}
