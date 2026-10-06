namespace Joule;

public sealed class CollectorWorker(StateService state, ILogger<CollectorWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await state.CollectAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { log.LogWarning("Collection failed; retaining last successful snapshot. Check connection settings."); }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
public sealed class AnalysisWorker(StateService state, InvestigationScheduler scheduler, AnalysisService analysis, ExperimentEvaluator experiments, ILogger<AnalysisWorker> log, DataStore? db = null) : BackgroundService
{
    /// <summary>How long shutdown waits for a running check to record that it was interrupted.</summary>
    public static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Before anything else runs: a check left Running by a crash or redeploy becomes Interrupted and resumes promptly, and the
        // one-off repairs of AI records run once.
        try
        {
            var zone = analysis.Zone;
            await state.MutateAsync(s =>
            {
                var recovered = AnalysisService.RecoverInterrupted(s, DateTimeOffset.UtcNow, zone);
                if (recovered > 0) log.LogInformation("Marked {Count} interrupted AI check(s) for resumption.", recovered);
                var repaired = AiDataRepairs.Apply(s, db);
                if (repaired.Total > 0) log.LogInformation("AI record repairs: {Summary}", repaired.Summary);
            }, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Startup recovery of AI checks failed: {Type}", e.GetType().Name); }
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await scheduler.TickAsync(DateTimeOffset.UtcNow, stoppingToken);
                await experiments.EvaluateAsync(stoppingToken);
                await state.AutoApplyAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { log.LogWarning("Analysis or experiment cycle failed. Current control state has been retained."); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        // The run is linked to ApplicationStopping, so it is already cancelling; give it a moment to write its Interrupted record.
        await analysis.WaitForRunAsync(ShutdownGrace);
    }
}
