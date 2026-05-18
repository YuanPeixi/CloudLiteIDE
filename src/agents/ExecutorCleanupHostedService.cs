namespace CloudLiteIDE.Agents;

public sealed class ExecutorCleanupHostedService : BackgroundService
{
    private readonly ExecutorRegistry _registry;

    public ExecutorCleanupHostedService(ExecutorRegistry registry)
    {
        _registry = registry;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            _registry.CleanupExpired();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
