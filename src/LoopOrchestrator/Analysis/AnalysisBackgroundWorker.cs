namespace LoopOrchestrator.Analysis;

/// <summary>
/// Timer-driven trigger for the self-improvement outer loop, interval from
/// ANALYSIS_INTERVAL_HOURS (default 168 = weekly) — exact structural mirror of
/// Loop/LoopBackgroundWorker.cs, just on a much slower cadence (this is occasional/expensive
/// analysis, not per-tender work). POST /analyze-now gets the same Scoped-per-run behavior for
/// free from ASP.NET Core's automatic per-request scope.
/// </summary>
public sealed class AnalysisBackgroundWorker(
    IServiceScopeFactory scopeFactory, AnalysisOptions options, ILogger<AnalysisBackgroundWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(options.AnalysisIntervalHours));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<AnalysisRunner>();
                await runner.TryRunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed analysis run shouldn't kill the timer — log and try again next tick.
                logger.LogError(ex, "Scheduled analysis run failed.");
            }
        }
    }
}
