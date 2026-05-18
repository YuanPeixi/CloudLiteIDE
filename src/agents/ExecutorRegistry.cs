using System.Collections.Concurrent;
using System.Threading.Channels;

namespace CloudLiteIDE.Agents;

public sealed class ExecutorRegistry
{
    private sealed class ExecutorSession
    {
        public required IExecutor Executor { get; init; }
        public required Channel<ExecutionEvent> EventChannel { get; init; }
        public required SemaphoreSlim RunLock { get; init; }
        public required DateTimeOffset LeaseExpiresAt { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, ExecutorSession> _sessions = new();
    private readonly ExecutorSettings _settings;
    private readonly Func<DateTimeOffset> _utcNow;

    public ExecutorRegistry(ExecutorSettings settings, Func<DateTimeOffset>? utcNow = null)
    {
        _settings = settings;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public (Guid ExecutorId, int LeaseTtlSeconds) Create()
    {
        var id = Guid.NewGuid();
        IExecutor executor = _settings.Mode switch
        {
            ExecutorMode.Native => new NativeExecutor(id, _settings),
            _ => throw new NotSupportedException($"Unsupported executor mode: {_settings.Mode}")
        };

        _sessions[id] = new ExecutorSession
        {
            Executor = executor,
            EventChannel = Channel.CreateUnbounded<ExecutionEvent>(),
            RunLock = new SemaphoreSlim(1, 1),
            LeaseExpiresAt = _utcNow().AddSeconds(_settings.LeaseTtlSeconds)
        };

        return (id, _settings.LeaseTtlSeconds);
    }

    public bool Renew(Guid executorId)
    {
        if (!TryGetActive(executorId, out var session))
        {
            return false;
        }

        session.LeaseExpiresAt = _utcNow().AddSeconds(_settings.LeaseTtlSeconds);
        return true;
    }

    public bool Release(Guid executorId)
    {
        if (_sessions.TryRemove(executorId, out var session))
        {
            session.EventChannel.Writer.TryComplete();
            session.Executor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            session.RunLock.Dispose();
            return true;
        }

        return false;
    }

    public bool TrySubscribe(Guid executorId, out ChannelReader<ExecutionEvent>? reader)
    {
        if (!TryGetActive(executorId, out var session))
        {
            reader = null;
            return false;
        }

        reader = session.EventChannel.Reader;
        return true;
    }

    public async ValueTask<ExecutionResult?> ExecuteAsync(Guid executorId, ExecuteRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetActive(executorId, out var session))
        {
            return null;
        }

        if (!await session.RunLock.WaitAsync(0, cancellationToken))
        {
            await session.EventChannel.Writer.WriteAsync(
                new ExecutionEvent(StreamEventType.Error, "Another execution is already in progress for this executor.", _utcNow(), Final: true),
                cancellationToken);
            return new ExecutionResult(false, "Executor is busy.");
        }

        try
        {
            await session.EventChannel.Writer.WriteAsync(new ExecutionEvent(StreamEventType.Status, "Execution started.", _utcNow()), cancellationToken);
            var result = await session.Executor.ExecuteAsync(request, session.EventChannel.Writer, cancellationToken);
            await session.EventChannel.Writer.WriteAsync(
                new ExecutionEvent(StreamEventType.Result, result.Message + (result.ExitCode is { } c ? $" (exit={c})" : string.Empty), _utcNow(), Final: true),
                cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            await session.EventChannel.Writer.WriteAsync(
                new ExecutionEvent(StreamEventType.Error, $"Internal execution error: {ex.Message}", _utcNow(), Final: true),
                cancellationToken);
            return new ExecutionResult(false, "Internal execution error.");
        }
        finally
        {
            session.RunLock.Release();
        }
    }

    public int CleanupExpired()
    {
        var now = _utcNow();
        var expired = _sessions
            .Where(entry => entry.Value.LeaseExpiresAt <= now)
            .Select(entry => entry.Key)
            .ToArray();

        foreach (var id in expired)
        {
            Release(id);
        }

        return expired.Length;
    }

    private bool TryGetActive(Guid executorId, out ExecutorSession session)
    {
        if (_sessions.TryGetValue(executorId, out session!))
        {
            if (session.LeaseExpiresAt > _utcNow())
            {
                return true;
            }

            Release(executorId);
        }

        session = null!;
        return false;
    }
}
