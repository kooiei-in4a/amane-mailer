using System.Threading.Channels;

namespace Amane.Mailer.Queue;

public interface IMailRequestQueue
{
    ChannelReader<WorkAvailableSignal> Reader { get; }

    bool TrySignalWorkAvailable();
}
