using System.Threading.Channels;

namespace Synthesia2Midi.Services;

/// File d'attente des conversions à traiter (brief §"Jobs" : BackgroundService + Channel&lt;T&gt;).
public class ConversionQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ValueTask EnqueueAsync(Guid conversionId, CancellationToken cancellationToken = default)
        => _channel.Writer.WriteAsync(conversionId, cancellationToken);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
