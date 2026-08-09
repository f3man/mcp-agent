namespace LoopOrchestrator.Loop;

/// <summary>
/// Timer-driven trigger for the loop, interval from LOOP_INTERVAL_MINUTES (default 360). Creates
/// an explicit DI scope per tick to resolve the Scoped LoopRunner — the standard pattern for using
/// scoped services from a singleton BackgroundService. The /run-now endpoint gets the same
/// Scoped-per-run behavior for free from ASP.NET Core's automatic per-request scope.
/// </summary>
public sealed class LoopBackgroundWorker(
    IServiceScopeFactory scopeFactory, LoopOptions options, ILogger<LoopBackgroundWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.LoopIntervalMinutes));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<LoopRunner>();
                await runner.TryRunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed run shouldn't kill the timer — log and try again next tick.
                logger.LogError(ex, "Scheduled loop run failed.");
            }
        }
    }
}
