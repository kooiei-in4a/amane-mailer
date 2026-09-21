using System.Globalization;
using System.Security.Cryptography;
using Age;
using Age.Recipients;

namespace Amane.Mailer.Admin;

internal static class SettingsBackupCrypto
{
    internal const int MaxEncryptedBytes = 1024 * 1024;

    internal static bool TryEncrypt(
        byte[] plaintext,
        string passphrase,
        out byte[]? encrypted,
        int workFactor = 18)
    {
        encrypted = null;
        if (plaintext.Length is 0 or > SettingsBackupFormat.MaxPlaintextBytes
            || !SettingsBackupFormat.IsValidPassphrase(passphrase))
        {
            return false;
        }

        try
        {
            using var input = new MemoryStream(plaintext, writable: false);
            using var output = new MemoryStream();
            AgeEncrypt.Encrypt(input, output, new ScryptRecipient(passphrase, workFactor));
            if (output.Length is 0 or > MaxEncryptedBytes)
                return false;

            encrypted = output.ToArray();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            encrypted = null;
            return false;
        }
    }

    internal static bool TryDecryptAndValidate(
        byte[] encrypted,
        string passphrase,
        out SettingsBackupPayload? payload)
    {
        payload = null;
        if (encrypted.Length is 0 or > MaxEncryptedBytes
            || !SettingsBackupFormat.IsValidPassphrase(passphrase))
        {
            return false;
        }

        try
        {
            using var input = new MemoryStream(encrypted, writable: false);
            var header = AgeHeader.Parse(input);
            if (!HasBoundedPassphraseRecipient(header))
                return false;

            input.Position = 0;
            byte[] plaintext;
            using (var output = new ZeroingMemoryStream())
            {
                AgeEncrypt.Decrypt(input, output, new ScryptRecipient(passphrase));
                if (output.Length is 0 or > SettingsBackupFormat.MaxPlaintextBytes)
                    return false;

                plaintext = output.ToArray();
            }

            try
            {
                return SettingsBackupFormat.TryDeserializeAndValidate(plaintext, out payload);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            payload = null;
            return false;
        }
    }

    private static bool HasBoundedPassphraseRecipient(AgeHeader header)
    {
        if (header.RecipientCount != 1 || header.Recipients.Count != 1)
            return false;

        var recipient = header.Recipients[0];
        if (!string.Equals(recipient.Type, "scrypt", StringComparison.Ordinal)
            || recipient.Args.Count != 2
            || !int.TryParse(
                recipient.Args[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var workFactor))
        {
            return false;
        }

        // age's default scrypt cost is 18. Reject larger hostile values before the KDF runs.
        return workFactor is >= 1 and <= 18;
    }

    private sealed class ZeroingMemoryStream : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing && TryGetBuffer(out var buffer) && buffer.Array is not null)
                CryptographicOperations.ZeroMemory(buffer.Array.AsSpan());

            base.Dispose(disposing);
        }
    }
}
