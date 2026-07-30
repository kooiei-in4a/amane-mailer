using System.Security.Cryptography;

namespace Amane.Mailer.Setup;

/// <summary>
/// Internal correlation capability for one Admin bootstrap attempt. It is persisted only in
/// recorded metadata and owner-only workflow state and must never be copied to public results.
/// </summary>
internal readonly record struct AdminBootstrapOperationId
{
    internal const int HexLength = 64;

    private AdminBootstrapOperationId(string value)
    {
        Value = value;
    }

    internal string Value { get; }

    internal static AdminBootstrapOperationId Create()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return new AdminBootstrapOperationId(Convert.ToHexString(bytes).ToLowerInvariant());
    }

    internal static bool TryParse(string? value, out AdminBootstrapOperationId operationId)
    {
        operationId = default;
        if (value is null || value.Length != HexLength)
            return false;

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }

        operationId = new AdminBootstrapOperationId(value);
        return true;
    }

    public override string ToString() => "[redacted]";
}
