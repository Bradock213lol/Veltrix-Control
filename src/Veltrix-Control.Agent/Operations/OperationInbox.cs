using System.Threading.Channels;
using VeltrixControl.Contracts;

namespace VeltrixControl.Agent.Operations;

public sealed class OperationInbox
{
    private readonly Channel<OperationAssignment> _channel = Channel.CreateUnbounded<OperationAssignment>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public int Count => _channel.Reader.Count;

    public ValueTask EnqueueAsync(OperationAssignment operation, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(operation, cancellationToken);

    public IAsyncEnumerable<OperationAssignment> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
