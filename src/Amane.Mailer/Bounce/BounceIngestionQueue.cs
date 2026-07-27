using System.Threading.Channels;

namespace Amane.Mailer.Bounce;

public interface IBounceIngestionQueue
{
    ChannelReader<BounceIngestionWorkAvailableSignal> Reader { get; }

    bool TrySignalWorkAvailable();
}

public readonly struct BounceIngestionWorkAvailableSignal;

public sealed class BounceIngestionQueue : IBounceIngestionQueue
{
    private const int Capacity = 1;

    private readonly Channel<BounceIngestionWorkAvailableSignal> _channel =
        Channel.CreateBounded<BounceIngestionWorkAvailableSignal>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            });

    public ChannelReader<BounceIngestionWorkAvailableSignal> Reader => _channel.Reader;

    public bool TrySignalWorkAvailable() => _channel.Writer.TryWrite(default);
}
