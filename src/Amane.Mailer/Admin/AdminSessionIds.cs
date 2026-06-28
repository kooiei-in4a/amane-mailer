using System.Security.Cryptography;

namespace Amane.Mailer.Admin;

public static class AdminSessionIds
{
    public static string CreateNew()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
