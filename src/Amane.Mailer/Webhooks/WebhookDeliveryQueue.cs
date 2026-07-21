using System.Threading.Channels;

namespace Amane.Mailer.Webhooks;

public interface IWebhookDeliveryQueue
{
    ChannelReader<WebhookWorkAvailableSignal> Reader { get; }

    bool TrySignalWorkAvailable();
}

public readonly struct WebhookWorkAvailableSignal;

public sealed class WebhookDeliveryQueue : IWebhookDeliveryQueue
{
    private const int Capacity = 1;

    private readonly Channel<WebhookWorkAvailableSignal> _channel = Channel.CreateBounded<WebhookWorkAvailableSignal>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    public ChannelReader<WebhookWorkAvailableSignal> Reader => _channel.Reader;

    public bool TrySignalWorkAvailable() => _channel.Writer.TryWrite(default);
}
