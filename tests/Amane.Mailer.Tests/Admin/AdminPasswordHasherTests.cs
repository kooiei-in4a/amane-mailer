using System.Security.Cryptography;
using Amane.Mailer.Admin;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminPasswordHasherTests
{
    [Fact]
    public void Generated_hash_is_supported_and_verifiable()
    {
        const string password = "generated-password-for-bounds-test";
        var hash = AdminPasswordHasher.Hash(password);

        Assert.True(AdminPasswordHasher.IsSupportedHash(hash));
        Assert.True(AdminPasswordHasher.Verify(password, hash));
        Assert.StartsWith("pbkdf2:sha256:600000:", hash, StringComparison.Ordinal);
    }

    [Fact]
    public void Boundary_iteration_counts_are_supported()
    {
        Assert.True(AdminPasswordHasher.IsSupportedHash(
            Encode(AdminPasswordHasher.MinIterations, saltBytes: 16, hashBytes: 32)));
        Assert.True(AdminPasswordHasher.IsSupportedHash(
            Encode(AdminPasswordHasher.MaxIterations, saltBytes: 16, hashBytes: 32)));
    }

    [Fact]
    public void Boundary_salt_and_hash_lengths_are_supported()
    {
        Assert.True(AdminPasswordHasher.IsSupportedHash(
            Encode(AdminPasswordHasher.MinIterations, saltBytes: AdminPasswordHasher.MinSaltSize, hashBytes: AdminPasswordHasher.MinHashSize)));
        Assert.True(AdminPasswordHasher.IsSupportedHash(
            Encode(AdminPasswordHasher.MinIterations, saltBytes: AdminPasswordHasher.MaxSaltSize, hashBytes: AdminPasswordHasher.MaxHashSize)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(599_999)]
    [InlineData(10_000_001)]
    public void Out_of_range_iterations_are_rejected(int iterations)
    {
        var encoded = Encode(iterations, saltBytes: 16, hashBytes: 32);

        Assert.False(AdminPasswordHasher.IsSupportedHash(encoded));
        Assert.False(AdminPasswordHasher.Verify("password", encoded));
    }

    [Theory]
    [InlineData(15, 32)]
    [InlineData(65, 32)]
    [InlineData(16, 31)]
    [InlineData(16, 65)]
    public void Out_of_range_salt_or_hash_lengths_are_rejected(int saltBytes, int hashBytes)
    {
        var encoded = Encode(AdminPasswordHasher.MinIterations, saltBytes, hashBytes);

        Assert.False(AdminPasswordHasher.IsSupportedHash(encoded));
        Assert.False(AdminPasswordHasher.Verify("password", encoded));
    }

    [Fact]
    public void Validate_rejects_legacy_weak_iteration_hash()
    {
        var options = new MailerAdminOptions
        {
            Enabled = true,
            Username = "admin",
            PasswordHash = Encode(100_000, saltBytes: 16, hashBytes: 32),
            AllowedLocalAddress = "127.0.0.1",
        };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("AMANE_ADMIN_PASSWORD_HASH", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Legacy weaker hashes are rejected", exception.Message, StringComparison.Ordinal);
        Assert.Contains(AdminPasswordHasher.MinIterations.ToString(System.Globalization.CultureInfo.InvariantCulture), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_accepts_current_generation_hash()
    {
        var options = new MailerAdminOptions
        {
            Enabled = true,
            Username = "admin",
            PasswordHash = AdminPasswordHasher.Hash("password"),
            AllowedLocalAddress = "127.0.0.1",
        };

        options.Validate();
    }

    private static string Encode(int iterations, int saltBytes, int hashBytes)
    {
        var salt = new byte[saltBytes];
        var hash = new byte[hashBytes];
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(hash);

        return string.Join(
            ':',
            "pbkdf2",
            "sha256",
            iterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }
}
