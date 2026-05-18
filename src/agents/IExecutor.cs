using System.Threading.Channels;

namespace CloudLiteIDE.Agents;

public interface IExecutor : IAsyncDisposable
{
    Guid ExecutorId { get; }
    string AnonymousToken { get; }
    ValueTask<ExecutionResult> ExecuteAsync(ExecuteRequest request, ChannelWriter<ExecutionEvent> streamWriter, CancellationToken cancellationToken);
}
