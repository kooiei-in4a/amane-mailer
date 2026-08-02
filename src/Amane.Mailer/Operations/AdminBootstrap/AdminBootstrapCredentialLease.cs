using System.Security.Cryptography;

namespace Amane.Mailer.Operations.AdminBootstrap;

/// <summary>
/// Best-effort short-lived mutable password buffer. Creating the string required by the existing
/// hasher/login API necessarily creates an immutable managed copy; callers must keep that copy in
/// the narrowest possible scope and never persist it.
/// </summary>
internal sealed class AdminBootstrapCredentialLease : IDisposable
{
    private char[]? _password;

    internal AdminBootstrapCredentialLease(ReadOnlySpan<char> password)
    {
        if (password.IsEmpty)
            throw new ArgumentException("Admin password is required.", nameof(password));

        _password = password.ToArray();
    }

    internal string Materialize()
    {
        ObjectDisposedException.ThrowIf(_password is null, this);
        return new string(_password);
    }

    public void Dispose()
    {
        if (_password is null)
            return;

        CryptographicOperations.ZeroMemory(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(_password.AsSpan()));
        _password = null;
    }
}
