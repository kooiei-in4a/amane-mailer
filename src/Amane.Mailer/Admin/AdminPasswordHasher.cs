using System.Security.Cryptography;

namespace Amane.Mailer.Admin;

public static class AdminPasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 600_000;

    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        Span<byte> salt = stackalloc byte[SaltSize];
        RandomNumberGenerator.Fill(salt);

        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        try
        {
            return string.Join(
                ':',
                "pbkdf2",
                "sha256",
                Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToBase64String(salt),
                Convert.ToBase64String(hash));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public static bool Verify(string password, string encodedHash)
    {
        if (!TryParse(encodedHash, out var parsed))
            return false;

        var computed = Rfc2898DeriveBytes.Pbkdf2(
            password,
            parsed.Salt,
            parsed.Iterations,
            HashAlgorithmName.SHA256,
            parsed.Hash.Length);

        try
        {
            return CryptographicOperations.FixedTimeEquals(computed, parsed.Hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(computed);
            CryptographicOperations.ZeroMemory(parsed.Salt);
            CryptographicOperations.ZeroMemory(parsed.Hash);
        }
    }

    public static bool IsSupportedHash(string encodedHash) =>
        TryParse(encodedHash, out var parsed) && parsed.Dispose();

    private static bool TryParse(string encodedHash, out ParsedHash parsed)
    {
        parsed = default;
        var parts = encodedHash.Split(':');
        if (parts.Length != 5)
            return false;

        if (!string.Equals(parts[0], "pbkdf2", StringComparison.Ordinal)
            || !string.Equals(parts[1], "sha256", StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(
                parts[2],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var iterations)
            || iterations <= 0)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[3]);
            var hash = Convert.FromBase64String(parts[4]);
            if (salt.Length < SaltSize || hash.Length < HashSize)
            {
                CryptographicOperations.ZeroMemory(salt);
                CryptographicOperations.ZeroMemory(hash);
                return false;
            }

            parsed = new ParsedHash(iterations, salt, hash);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private readonly record struct ParsedHash(int Iterations, byte[] Salt, byte[] Hash)
    {
        public bool Dispose()
        {
            CryptographicOperations.ZeroMemory(Salt);
            CryptographicOperations.ZeroMemory(Hash);
            return true;
        }
    }
}
