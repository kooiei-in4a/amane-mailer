namespace Amane.Mailer.Operations;

/// <summary>
/// Cooperative Ctrl+C handling for long-running CLI commands. First CancelKeyPress
/// cancels the shared token without terminating the process; dispose removes the handler.
/// </summary>
internal sealed class MailerCliCancellation : IDisposable
{
    /// <summary>
    /// Exit code for cooperative Ctrl+C / cancellation. Uses the conventional
    /// <c>128 + SIGINT</c> value so it does not collide with success (0),
    /// unavailable/unhealthy (1), or usage error (2).
    /// </summary>
    public const int ExitCode = 130;

    private readonly CancellationTokenSource _cts;
    private readonly ConsoleCancelEventHandler _handler;
    private int _subscribed;
    private int _disposed;

    private MailerCliCancellation(bool attachConsoleHandler)
    {
        _cts = new CancellationTokenSource();
        _handler = OnCancelKeyPress;
        if (attachConsoleHandler)
        {
            Console.CancelKeyPress += _handler;
            Volatile.Write(ref _subscribed, 1);
        }
    }

    public CancellationToken Token => _cts.Token;

    internal bool IsHandlerSubscribedForTests => Volatile.Read(ref _subscribed) == 1;

    public static MailerCliCancellation Attach() => new(attachConsoleHandler: true);

    /// <summary>
    /// Test-only scope without registering <see cref="Console.CancelKeyPress"/>.
    /// </summary>
    internal static MailerCliCancellation CreateUnattachedForTests() =>
        new(attachConsoleHandler: false);

    /// <summary>
    /// Mirrors the Ctrl+C path without constructing <see cref="ConsoleCancelEventArgs"/>.
    /// </summary>
    internal void RequestCancelForTests() => RequestCancel();

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        // Keep the process alive so the awaiter can observe cancellation, roll back, and clean up.
        e.Cancel = true;
        RequestCancel();
    }

    private void RequestCancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Dispose raced with CancelKeyPress; nothing left to cancel.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _subscribed, 0) == 1)
        {
            Console.CancelKeyPress -= _handler;
        }

        _cts.Dispose();
    }
}
