using System.Threading.Channels;

namespace Amane.Mailer.Queue;

public sealed class MailRequestQueue : IMailRequestQueue
{
    private const int Capacity = 1;

    private readonly Channel<WorkAvailableSignal> _channel = Channel.CreateBounded<WorkAvailableSignal>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    public ChannelReader<WorkAvailableSignal> Reader => _channel.Reader;

    public bool TrySignalWorkAvailable() => _channel.Writer.TryWrite(default);
}
