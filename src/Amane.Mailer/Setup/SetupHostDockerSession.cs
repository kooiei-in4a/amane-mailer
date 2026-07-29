namespace Amane.Mailer.Setup;

/// <summary>
/// Holds APPLY.lock and the immutable Docker connection binding for one apply/setup session.
/// #450 owns how long the session lives across multiple adapter operations.
/// </summary>
public sealed class SetupHostDockerSession : IAsyncDisposable
{
    private readonly SetupApplyLock _applyLock;
    private bool _disposed;

    internal SetupHostDockerSession(
        TrustedSetupHostLayout layout,
        DockerConnectionBinding binding,
        SetupApplyLock applyLock)
    {
        Layout = layout;
        Binding = binding;
        _applyLock = applyLock;
    }

    public TrustedSetupHostLayout Layout { get; }
    public DockerConnectionBinding Binding { get; }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _applyLock.Dispose();
        return ValueTask.CompletedTask;
    }

    internal void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
