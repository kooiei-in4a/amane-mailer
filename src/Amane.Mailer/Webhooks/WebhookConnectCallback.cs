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

        var socket = new Socket(connectAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        try
        {
            await socket.ConnectAsync(new IPEndPoint(connectAddress, dnsEndPoint.Port), cancellationToken)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
