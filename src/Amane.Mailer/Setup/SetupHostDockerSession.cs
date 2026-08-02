namespace Amane.Mailer.Setup;

/// <summary>
/// Holds APPLY.lock, the immutable Docker connection binding, and the pinned input snapshots for
/// one apply/setup session. #450 owns how long the session lives across multiple adapter
/// operations, and which generation each ACTIVE-dependent operation is allowed to observe.
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

    /// <summary>ACTIVE-independent pin of allowlisted external inputs for this session.</summary>
    public SetupExternalInputSnapshot? ExternalInputs { get; private set; }

    /// <summary>ACTIVE-dependent composed environment pinned for one activation generation.</summary>
    public SetupComposeInputSnapshot? ComposeInputs { get; private set; }

    /// <summary>
    /// Set once <c>managed/tmp</c> has been proven to contain no unsafe residue and no stale
    /// mount verifiers. Effective inspection refuses to run before this is asserted.
    /// </summary>
    public bool StaleVerifiersPurged { get; private set; }

    internal void SetExternalInputs(SetupExternalInputSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ThrowIfDisposed();

        // Re-pinning invalidates any compose snapshot derived from the previous external layer.
        ClearComposeInputs();
        var previous = ExternalInputs;
        ExternalInputs = snapshot;
        if (!ReferenceEquals(previous, snapshot))
        {
            previous?.Dispose();
        }
    }

    internal void SetComposeInputs(SetupComposeInputSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ThrowIfDisposed();

        var previous = ComposeInputs;
        ComposeInputs = snapshot;
        if (!ReferenceEquals(previous, snapshot))
        {
            previous?.Dispose();
        }
    }

    internal void ClearComposeInputs()
    {
        var previous = ComposeInputs;
        ComposeInputs = null;
        previous?.Dispose();
    }

    internal void ClearSnapshots()
    {
        ClearComposeInputs();
        var external = ExternalInputs;
        ExternalInputs = null;
        external?.Dispose();
    }

    internal void MarkStaleVerifiersPurged() => StaleVerifiersPurged = true;

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        ClearSnapshots();
        StaleVerifiersPurged = false;
        _applyLock.Dispose();
        return ValueTask.CompletedTask;
    }

    internal void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
