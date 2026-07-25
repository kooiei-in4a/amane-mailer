using System.Net;
using System.Net.Sockets;

namespace Amane.Mailer.Webhooks;

internal static class WebhookConnectCallback
{
    internal static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var dnsEndPoint = context.DnsEndPoint;
        IPAddress connectAddress;

        if (context.InitialRequestMessage?.Options.TryGetValue(
                WebhookConnectionOptions.PinnedConnectAddress,
                out var pinnedAddress) == true)
        {
            connectAddress = pinnedAddress;
        }
        else if (!IPAddress.TryParse(dnsEndPoint.Host, out connectAddress!))
        {
            throw new HttpRequestException("Webhook delivery is missing a pinned connect address.");
        }

        // Ownership transfers to NetworkStream on success; keep a local so CA2000
        // can see dispose on every failure path and null-out after transfer.
        Socket? socket = null;
        try
        {
            socket = new Socket(connectAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            await socket.ConnectAsync(new IPEndPoint(connectAddress, dnsEndPoint.Port), cancellationToken)
                .ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);
            socket = null;
            return stream;
        }
        finally
        {
            socket?.Dispose();
        }
    }
}
