using VlessCheck.Core;

namespace VlessCheck.Checker;

public sealed class CheckRunner(Store store, ILogger<CheckRunner> log)
{
    public Progress Progress { get; } = new(store.DataDir);

    static string XrayPath()
    {
        if (Environment.GetEnvironmentVariable("VLESSCHECK_XRAY") is { Length: > 0 } path)
            return path;
        var found = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, "xray")).FirstOrDefault(File.Exists);
        return found ?? throw new InvalidOperationException("xray не найден: установите его или укажите VLESSCHECK_XRAY");
    }

    public async Task RunGroups(string only, CancellationToken ct)
    {
        var settings = store.Settings();
        var groups = store.Groups().Where(g => only == "*" || g.Name == only).ToList();
        var xray = new Xray(XrayPath(), log);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Progress.Update(s =>
        {
            s.Running = true;
            s.Started = now;
            s.Queue = groups.Select(g => g.Name).ToList();
        });
        try
        {
            foreach (var g in groups)
            {
                try
                {
                    await new GroupChecker(store, g, settings, Progress, xray, log).Run(ct);
                }
                catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
                {
                    log.LogError(e, "[{Group}] цикл упал", g.Name);
                }
            }
        }
        finally
        {
            Progress.Update(s =>
            {
                s.Running = false;
                s.Group = s.Stage = null;
                s.Done = s.Total = 0;
                s.Queue = [];
                s.LastRun = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            });
        }
    }
}
