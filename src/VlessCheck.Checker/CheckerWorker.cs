using VlessCheck.Core;

namespace VlessCheck.Checker;

/// <summary>Проверяет все группы раз в interval_minutes или сразу по кнопке «Проверить сейчас» из панели.
/// Когда сервис выключен в панели, не запускает проверки и прерывает идущую.</summary>
public sealed class CheckerWorker(Store store, CheckRunner runner, ILogger<CheckerWorker> log) : BackgroundService
{
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        double lastFull = 0;
        while (!ct.IsCancellationRequested)
        {
            var settings = store.Settings();
            if (!settings.Enabled)
            {
                store.PopRunRequest(); // нажатия «Проверить» в выключенном состоянии не копим
                await Task.Delay(Tick, ct);
                continue;
            }

            // интервал перечитываем каждый раз — его могут поменять в панели во время ожидания
            var nextRun = lastFull + Math.Max(settings.IntervalMinutes * 60, 60);
            if (runner.Progress.State.NextRun != nextRun)
                runner.Progress.Update(s => s.NextRun = nextRun);

            var only = store.PopRunRequest();
            if (only is null && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < nextRun)
            {
                await Task.Delay(Tick, ct);
                continue;
            }

            var started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var watcher = StopWhenDisabled(run);
            try
            {
                await runner.RunGroups(only ?? "*", run.Token);
                if ((only ?? "*") == "*")
                    lastFull = started;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("проверка прервана: сервис выключен в панели");
            }
            finally
            {
                await run.CancelAsync();
                await watcher;
            }
        }
    }

    async Task StopWhenDisabled(CancellationTokenSource run)
    {
        try
        {
            while (!run.IsCancellationRequested)
            {
                await Task.Delay(Tick, run.Token);
                if (!store.Settings().Enabled)
                    await run.CancelAsync();
            }
        }
        catch (OperationCanceledException) { }
    }
}
