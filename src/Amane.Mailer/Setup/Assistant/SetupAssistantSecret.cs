namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// Session-memory holder for a secret the operator typed. The value is kept in a mutable buffer
/// so it can be zeroed when the session ends, and it is never rendered, logged, or persisted.
/// </summary>
internal sealed class SetupAssistantSecret : IDisposable
{
    private char[] _buffer;
    private int _length;
    private bool _disposed;

    private SetupAssistantSecret(char[] buffer, int length)
    {
        _buffer = buffer;
        _length = length;
    }

    internal static SetupAssistantSecret Capture(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var buffer = new char[value.Length];
        value.CopyTo(buffer);
        return new SetupAssistantSecret(buffer, value.Length);
    }

    /// <summary>
    /// Materializes the value for a single typed-operation call. Callers must not store the
    /// returned string beyond the call, and must never place it in a response, log, or file.
    /// </summary>
    internal string Reveal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new string(_buffer, 0, _length);
    }

    /// <summary>
    /// Borrows the raw characters so a consumer that owns its own zeroing buffer (for example
    /// <c>AdminBootstrapCredentialLease</c>) can copy them without materializing a string.
    /// </summary>
    internal ReadOnlySpan<char> AsSpan()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _buffer.AsSpan(0, _length);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _buffer.AsSpan().Clear();
        _buffer = [];
        _length = 0;
    }
}
