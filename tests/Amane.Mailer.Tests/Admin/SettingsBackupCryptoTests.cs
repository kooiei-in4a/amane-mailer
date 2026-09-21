using System.Security.Cryptography;
using Amane.Mailer.Admin;

namespace Amane.Mailer.Tests.Admin;

public sealed class SettingsBackupCryptoTests
{
    private const string Passphrase = "test-only-settings-backup-passphrase";

    [Fact]
    public void Age_passphrase_round_trip_rejects_wrong_passphrase_and_tampering()
    {
        var plaintext = SettingsBackupFormat.Serialize(SettingsBackupFormatTests.CreatePayload());
        try
        {
            Assert.True(SettingsBackupCrypto.TryEncrypt(
                plaintext,
                Passphrase,
                out var encrypted,
                workFactor: 10));
            Assert.NotNull(encrypted);

            Assert.True(SettingsBackupCrypto.TryDecryptAndValidate(encrypted, Passphrase, out var payload));
            Assert.NotNull(payload);
            Assert.Equal("sender@example.com", Assert.Single(payload.Settings.Senders).Email);
            Assert.False(SettingsBackupCrypto.TryDecryptAndValidate(
                encrypted,
                "different-test-only-passphrase",
                out _));

            var tampered = encrypted.ToArray();
            tampered[^1] ^= 0x01;
            Assert.False(SettingsBackupCrypto.TryDecryptAndValidate(tampered, Passphrase, out _));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    [Fact]
    public void Rejects_invalid_passphrases_and_oversized_ciphertext()
    {
        Assert.False(SettingsBackupFormat.IsValidPassphrase("short"));
        Assert.False(SettingsBackupFormat.IsValidPassphrase(new string('x', 1025)));
        Assert.False(SettingsBackupCrypto.TryDecryptAndValidate(
            new byte[SettingsBackupCrypto.MaxEncryptedBytes + 1],
            Passphrase,
            out _));
    }
}
