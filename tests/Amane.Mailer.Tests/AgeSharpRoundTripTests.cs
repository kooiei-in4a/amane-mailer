using System.Text;
using Age;
using Age.Recipients;

namespace Amane.Mailer.Tests;

public sealed class AgeSharpRoundTripTests
{
    [Fact]
    public void PassphraseEncryptionProducesAgeV1AndRoundTrips()
    {
        const string passphrase = "test-only-example-passphrase";
        const string plaintext = "Amane Mailer AgeSharp gate";
        var recipient = new ScryptRecipient(passphrase, workFactor: 10);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(plaintext));
        using var encrypted = new MemoryStream();

        AgeEncrypt.Encrypt(input, encrypted, recipient);

        Assert.StartsWith(
            "age-encryption.org/v1\n",
            Encoding.UTF8.GetString(encrypted.ToArray()),
            StringComparison.Ordinal);

        encrypted.Position = 0;
        using var decrypted = new MemoryStream();
        AgeEncrypt.Decrypt(encrypted, decrypted, recipient);

        Assert.Equal(plaintext, Encoding.UTF8.GetString(decrypted.ToArray()));
    }
}
