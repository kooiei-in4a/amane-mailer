using System.Text.Json;
using Amane.Mailer.Configuration;
using Amane.Mailer.Json;

namespace Amane.Mailer.Operations;

public enum RegisteredSecretState
{
    /// <summary>Neither the ACS secret nor the platform sender file holds a value. Safe to proceed.</summary>
    Clean,

    /// <summary>Both the ACS secret and the platform sender file already hold a valid value.</summary>
    FullyRegistered,

    /// <summary>
    /// Exactly one of the two is registered, or one exists but is empty/unparseable/invalid. This
    /// covers both an interrupted prior run and unrelated file corruption; either way the command
    /// must fail closed and require a human to inspect the two paths rather than guess.
    /// </summary>
    PartialOrCorrupt,
}

public static class RegisteredSecretStateInspector
{
    public static RegisteredSecretState Inspect(string acsSecretPath, string platformSenderPath)
    {
        var acsState = InspectAcsSecret(acsSecretPath);
        var senderState = InspectPlatformSender(platformSenderPath);

        if (acsState == LeafState.Absent && senderState == LeafState.Absent)
        {
            return RegisteredSecretState.Clean;
        }

        if (acsState == LeafState.Present && senderState == LeafState.Present)
        {
            return RegisteredSecretState.FullyRegistered;
        }

        // Any other combination — including a Corrupt leaf paired with an Absent one — must not
        // be conflated with Clean. Falling through here is what forces the fail-closed stop.
        return RegisteredSecretState.PartialOrCorrupt;
    }

    private enum LeafState { Absent, Present, Corrupt }

    private static LeafState InspectAcsSecret(string path)
    {
        if (!File.Exists(path))
        {
            return LeafState.Absent;
        }

        var content = File.ReadAllText(path);
        return string.IsNullOrWhiteSpace(content) ? LeafState.Absent : LeafState.Present;
    }

    private static LeafState InspectPlatformSender(string path)
    {
        if (!File.Exists(path))
        {
            return LeafState.Absent;
        }

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return LeafState.Absent;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(text, MailerJsonContext.Default.PlatformSenderFile);
            if (parsed is null)
            {
                return LeafState.Corrupt;
            }

            parsed.Validate();
            return LeafState.Present;
        }
        catch
        {
            return LeafState.Corrupt;
        }
    }
}
