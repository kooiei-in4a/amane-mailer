using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Amane.Mailer.Tests.Spike525;

/// <summary>
/// Spike-only (#525) TCP relay sitting between an SMTP client under test and a real
/// Mailpit instance. Two purposes, both required because Mailpit's HTTP API is proven
/// (see #525 evidence) to reconstruct/augment messages rather than exposing literal
/// wire bytes:
///
/// 1. Wire capture: records the literal client-to-server bytes so header-leak claims
///    (e.g. "no Bcc: header on the wire") can be checked against ground truth instead
///    of Mailpit's API, which synthesizes a Bcc header from the envelope even when the
///    real bytes never contained one.
/// 2. Fault injection: can forward the full DATA payload to the real server (so Mailpit
///    genuinely receives/queues the message) while dropping the server's response and
///    severing the client connection, reproducing "provider accepted but caller never
///    saw the result" (unknown_after_submission, #525 S-06/S-08).
///
/// Not part of the public contract or production code path. Single in-flight
/// connection per relay instance; sufficient for spike fixtures.
/// </summary>
internal sealed class Spike525SmtpRelay : IAsyncDisposable
{
    private static readonly byte[] DataTerminator = "\r\n.\r\n"u8.ToArray();

    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    private readonly TcpListener _listener;
    private readonly MemoryStream _clientToServerCapture = new();
    private readonly object _captureLock = new();

    private bool _suppressResponseAfterDataTerminator;
    private Task? _acceptTask;
    private CancellationTokenSource? _cts;

    internal Spike525SmtpRelay(string upstreamHost, int upstreamPort)
    {
        _upstreamHost = upstreamHost;
        _upstreamPort = upstreamPort;
        _listener = new TcpListener(IPAddress.Loopback, 0);
    }

    /// <summary>
    /// When true, the relay forwards all client bytes to the real server (so the
    /// server fully receives them) but, once the SMTP DATA terminator has been
    /// observed on the wire, stops relaying server-to-client bytes and closes the
    /// client-facing socket without delivering the final response line.
    /// </summary>
    internal bool SuppressResponseAfterDataTerminator
    {
        get => _suppressResponseAfterDataTerminator;
        set => _suppressResponseAfterDataTerminator = value;
    }

    internal int ListenPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    internal void Start()
    {
        _listener.Start();
        _cts = new CancellationTokenSource();
        _acceptTask = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>Literal bytes the client wrote toward the server, captured independently of Mailpit's API.</summary>
    internal byte[] GetCapturedClientToServerBytes()
    {
        lock (_captureLock)
        {
            return _clientToServerCapture.ToArray();
        }
    }

    internal bool CapturedBytesContainHeader(string headerName)
    {
        var bytes = GetCapturedClientToServerBytes();
        var text = Encoding.ASCII.GetString(bytes);
        // Only inspect the DATA section (after the first blank-line-delimited SMTP command block
        // ends and payload begins) is unnecessary for a header-name search: SMTP commands
        // themselves never contain a colon-terminated header token, so a direct search is safe.
        foreach (var line in text.Split("\r\n"))
        {
            if (line.StartsWith(headerName + ":", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(_upstreamHost, _upstreamPort, cancellationToken);

            await using var clientStream = client.GetStream();
            await using var upstreamStream = upstream.GetStream();

            var suppressGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var clientToServer = PumpClientToServerAsync(clientStream, upstreamStream, suppressGate, cancellationToken);
            var serverToClient = PumpServerToClientAsync(upstreamStream, clientStream, suppressGate, cancellationToken);

            await Task.WhenAll(clientToServer, serverToClient);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Relay disposed; expected.
        }
        catch (Exception)
        {
            // Best-effort relay for a single spike fixture connection; surfaced to the
            // caller via the SMTP client's own failure (connection reset), which is
            // exactly the ambiguous signal S-06 is probing.
        }
    }

    private async Task PumpClientToServerAsync(
        Stream from,
        Stream to,
        TaskCompletionSource suppressGate,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var tailWindow = new byte[DataTerminator.Length];
        var tailWindowLength = 0;

        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                lock (_captureLock)
                {
                    _clientToServerCapture.Write(buffer, 0, read);
                }

                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await to.FlushAsync(cancellationToken);

                if (_suppressResponseAfterDataTerminator && !suppressGate.Task.IsCompleted)
                {
                    (tailWindowLength) = AppendAndScanForTerminator(
                        tailWindow, tailWindowLength, buffer, read, DataTerminator);
                    if (tailWindowLength < 0)
                    {
                        suppressGate.TrySetResult();
                    }
                }
            }
        }
        catch (Exception)
        {
            // Client closed or relay torn down; server pump loop observes this via its own read.
        }
        finally
        {
            suppressGate.TrySetResult();
        }
    }

    /// <summary>Rolling-window scan for <paramref name="terminator"/> across chunk boundaries. Returns -1 (sentinel) once found.</summary>
    private static int AppendAndScanForTerminator(
        byte[] window, int windowLength, byte[] chunk, int chunkLength, byte[] terminator)
    {
        // Simple approach sufficient for spike fixtures: concatenate window + chunk and scan.
        // Fixture payloads are small (KB-scale synthetic bodies/attachments), so this is not
        // performance-critical.
        var combined = new byte[windowLength + chunkLength];
        Buffer.BlockCopy(window, 0, combined, 0, windowLength);
        Buffer.BlockCopy(chunk, 0, combined, windowLength, chunkLength);

        for (var i = 0; i <= combined.Length - terminator.Length; i++)
        {
            var match = true;
            for (var j = 0; j < terminator.Length; j++)
            {
                if (combined[i + j] != terminator[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return -1;
            }
        }

        var keep = Math.Min(terminator.Length, combined.Length);
        Buffer.BlockCopy(combined, combined.Length - keep, window, 0, keep);
        return keep;
    }

    private async Task PumpServerToClientAsync(
        Stream from,
        Stream to,
        TaskCompletionSource suppressGate,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (_suppressResponseAfterDataTerminator && suppressGate.Task.IsCompleted)
                {
                    // DATA terminator already crossed the wire toward the real server:
                    // the server has (or will imminently have) processed/queued the
                    // message. Drop this and all further server bytes so the client
                    // never observes a completion response, then sever the connection.
                    break;
                }

                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await to.FlushAsync(cancellationToken);
            }
        }
        catch (Exception)
        {
            // Expected once the client-facing socket is severed below.
        }
        finally
        {
            if (_suppressResponseAfterDataTerminator)
            {
                try
                {
                    to.Close();
                }
                catch (Exception)
                {
                    // Best-effort severance.
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener.Stop();
        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask;
            }
            catch (Exception)
            {
                // Swallow: teardown-time relay faults are expected and irrelevant to the fixture result.
            }
        }

        _cts?.Dispose();
        await _clientToServerCapture.DisposeAsync();
    }
}
