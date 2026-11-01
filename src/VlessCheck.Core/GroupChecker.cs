using System.Text;
using Microsoft.Extensions.Logging;

namespace VlessCheck.Core;

public sealed class NodeHistory
{
    public List<int> Checks { get; set; } = [];
    public double LastOk { get; set; }
    public double Seen { get; set; }
}

public sealed class NodeReport
{
    public int Rank { get; set; }
    public string Name { get; set; } = "";
    public double Score { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public int LatencyMs { get; set; }
    public double OkRatio { get; set; }
    public double SpeedMbps { get; set; }
    public string Sites { get; set; } = "";
    public int PageMs { get; set; }
    public int DirectMs { get; set; }
    public string ExitIp { get; set; } = "";
    public string Country { get; set; } = "";
    public string Uptime { get; set; } = "";
    public string OriginalName { get; set; } = "";
}

public sealed class GroupStatus
{
    public double UpdatedTs { get; set; }
    public int DurationS { get; set; }
    public int Total { get; set; }
    public int TcpAlive { get; set; }
    public int XrayValid { get; set; }
    public int Working { get; set; }
    public int Selected { get; set; }
    public Dictionary<string, int> Countries { get; set; } = [];
    public List<NodeReport> Nodes { get; set; } = [];
}

/// <summary>Состояние воркера в progress.json — его читает api для панели.</summary>
public sealed class ProgressState
{
    public bool Running { get; set; }
    public string? Group { get; set; }
    public string? Stage { get; set; }
    public int Done { get; set; }
    public int Total { get; set; }
    public double Started { get; set; }
    public double LastRun { get; set; }
    public double NextRun { get; set; }
    public List<string> Queue { get; set; } = [];

    public static string PathIn(string dataDir) => System.IO.Path.Combine(dataDir, "progress.json");
}

public sealed class Progress(string dataDir)
{
    readonly Lock _lock = new();
    public ProgressState State { get; } = Json.Load<ProgressState>(ProgressState.PathIn(dataDir)) ?? new();

    public void Update(Action<ProgressState> change)
    {
        lock (_lock)
        {
            change(State);
            Json.Save(ProgressState.PathIn(dataDir), State);
        }
    }

    public void Stage(string group, string stage, int done = 0, int total = 0) => Update(s =>
    {
        s.Group = group;
        s.Stage = stage;
        s.Done = done;
        s.Total = total;
    });
}

/// <summary>Одна группа подписок: загрузка, проверка, рейтинг с историей, запись подписки.</summary>
public sealed class GroupChecker(Store store, Group group, Settings t, Progress progress, Xray xray, ILogger log)
{
    const int HistoryLen = 24;                         // 12 часов при интервале 30 минут
    static readonly double ForgetAfter = 7 * 86400;    // пропавшие из подписок узлы забываем через неделю

    readonly string _dir = store.GroupDir(group.Name);

    static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public static string Flag(string cc) =>
        cc.Length == 2 && cc.All(char.IsAsciiLetter)
            ? string.Concat(cc.ToUpperInvariant().Select(c => char.ConvertFromUtf32(0x1F1E6 + c - 'A')))
            : "🏳";

    /// <summary>0..100: стабильность 40 + 60 поровну между включёнными способами проверки (каждый даёт 0..1).</summary>
    public static double Score(ProbeResult r, List<int> checks, Settings t)
    {
        var uptime = (checks.Sum() + 1.0) / (checks.Count + 2); // новичкам не верим на слово
        var now = t.CheckProxyPing ? r.OkRatio : r.SitesTotal == 0 ? 1 : (double)r.SitesOk / r.SitesTotal;
        var stability = 40 * (0.5 * now + 0.5 * uptime);

        var parts = new List<double>();
        if (t.CheckDirectPing)
            parts.Add(Math.Clamp((300.0 - r.DirectMs) / 280, 0, 1));
        if (t.CheckProxyPing)
            parts.Add(Math.Clamp((1500.0 - r.Latency) / 1450, 0, 1));
        if (t.CheckSites && r.SitesTotal > 0)
            parts.Add((double)r.SitesOk / r.SitesTotal * (0.5 + 0.5 * Math.Clamp((4000.0 - r.PageMs) / 3500, 0, 1)));
        if (t.CheckSpeed)
            parts.Add(Math.Min(r.Speed, t.SpeedCapMbps) / t.SpeedCapMbps);
        return Math.Round(stability + (parts.Count == 0 ? 0 : 60 * parts.Average()), 1);
    }

    void Stage(string stage, int done = 0, int total = 0) => progress.Stage(group.Name, stage, done, total);

    public async Task Run(CancellationToken ct)
    {
        var started = Now();
        Directory.CreateDirectory(_dir);
        if (group.Subscriptions.Count == 0)
        {
            log.LogInformation("[{Group}] нет источников — добавьте подписки в панели", group.Name);
            return;
        }
        Stage("загрузка подписок");

        string? ownIp;
        try
        {
            ownIp = Probe.ParseTrace((await Probe.HttpGet(t.TraceUrl, null, 15, ct)).Body).GetValueOrDefault("ip");
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogError("[{Group}] нет прямого интернета ({Error}), цикл пропущен", group.Name, e.Message);
            return;
        }

        log.LogInformation("[{Group}] начинаю цикл", group.Name);
        var nodes = await Links.Gather(group.Subscriptions.Select(s => s.Url), log, ct);
        log.LogInformation("уникальных vless: {Count}", nodes.Count);

        Stage("проверка портов", 0, nodes.Count);
        var directMs = new double?[nodes.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, nodes.Count), new ParallelOptions { MaxDegreeOfParallelism = 100, CancellationToken = ct },
            async (i, c) => directMs[i] = await Probe.TcpPing(nodes[i], t.TcpTimeout, c));
        var alive = nodes.Where((_, i) => directMs[i] is not null).ToList();
        var direct = nodes.Select((v, i) => (v.Key, Ms: directMs[i])).Where(x => x.Ms is not null)
                          .ToDictionary(x => x.Key, x => (int)Math.Round(x.Ms!.Value));
        log.LogInformation("TCP-порт открыт: {Count}", alive.Count);

        var entries = new List<Entry>();
        foreach (var v in alive)
        {
            try
            {
                entries.Add(new Entry(v, XrayConfig.Outbound(v)));
            }
            catch (Exception e) when (e is ArgumentException or System.Text.Json.JsonException)
            {
                log.LogDebug("пропуск {Host}: {Error}", v.Host, e.Message);
            }
        }
        Stage("проверка конфигов xray", 0, entries.Count);
        var valid = new List<Entry>();
        for (var i = 0; i < entries.Count; i += t.BatchSize)
        {
            valid.AddRange(await xray.ValidSubset(entries.Skip(i).Take(t.BatchSize).ToList(), ct));
            Stage("проверка конфигов xray", Math.Min(i + t.BatchSize, entries.Count), entries.Count);
        }
        log.LogInformation("принято xray: {Count}", valid.Count);

        var lat = await Probe.RunBatches(xray, valid, t.BatchSize, t.Workers, port => Probe.Latency(port, t, ownIp, ct),
                                         (d, n) => Stage("подключение и задержка", d, n), log, ct);
        var working = valid.Zip(lat).Where(x => x.Second is not null).Select(x => (x.First, R: x.Second!)).ToList();
        foreach (var w in working)
            w.R.DirectMs = direct.GetValueOrDefault(w.First.Link.Key);
        log.LogInformation("работают через прокси: {Count}", working.Count);

        var countries = working.GroupBy(w => w.R.Country).ToDictionary(g => g.Key, g => g.Count());
        var excluded = t.ExcludeCountries.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (excluded.Count > 0)
        {
            working = working.Where(w => !excluded.Contains(w.R.Country)).ToList();
            log.LogInformation("после исключения стран {Countries}: {Count}", string.Join(",", excluded.Order()), working.Count);
        }

        if (t.CheckSites)
        {
            // конфиг, через который не открылся ни один настоящий сайт, считаем нерабочим
            var sites = await Probe.RunBatches<(int Ok, int AvgMs)?>(xray, working.Select(w => w.First).ToList(), t.BatchSize, t.Workers,
                                                                     async port => await Probe.Sites(port, t, ct),
                                                                     (d, n) => Stage("загрузка сайтов", d, n), log, ct);
            for (var i = 0; i < working.Count; i++)
            {
                working[i].R.SitesTotal = t.SiteUrls.Count;
                (working[i].R.SitesOk, working[i].R.PageMs) = sites[i] ?? (0, 0);
            }
            working = working.Where(w => w.R.SitesOk > 0).ToList();
            log.LogInformation("открывают сайты: {Count}", working.Count);
        }

        if (t.CheckSpeed)
        {
            // скорость меряем только у лучших по задержке и малым числом потоков, чтобы не забить свой канал
            working = working.OrderByDescending(w => w.R.OkRatio).ThenBy(w => w.R.Latency).ToList();
            var top = working.Take(t.SpeedTestTop).ToList();
            var speeds = await Probe.RunBatches<double?>(xray, top.Select(w => w.First).ToList(), t.BatchSize, t.SpeedWorkers,
                                                         async port => await Probe.Speed(port, t, ct),
                                                         (d, n) => Stage("замер скорости", d, n), log, ct);
            for (var i = 0; i < top.Count; i++)
                top[i].R.Speed = speeds[i] ?? 0;
        }

        var now = Now();
        var statePath = Path.Combine(_dir, "state.json");
        var state = Json.Load<Dictionary<string, NodeHistory>>(statePath) ?? [];
        var results = working.ToDictionary(w => w.First.Link.Key, w => (w.First.Link, w.R));
        foreach (var v in nodes)
        {
            var h = state.TryGetValue(v.Key, out var existing) ? existing : state[v.Key] = new NodeHistory();
            var ok = results.ContainsKey(v.Key);
            h.Checks = h.Checks.Append(ok ? 1 : 0).TakeLast(HistoryLen).ToList();
            if (ok)
                h.LastOk = now;
            h.Seen = now;
        }
        state = state.Where(kv => now - kv.Value.Seen < ForgetAfter).ToDictionary();

        var ranked = results.Values.Select(x =>
        {
            x.R.Score = Score(x.R, state[x.Link.Key].Checks, t);
            return x;
        }).OrderByDescending(x => x.R.Score).ToList();

        // сначала разнообразие по серверам, потом добиваем до top_n лучшими из оставшихся
        var perHost = new Dictionary<string, int>();
        var chosen = new List<(VlessLink Link, ProbeResult R)>();
        var rest = new List<(VlessLink Link, ProbeResult R)>();
        foreach (var x in ranked)
        {
            if (perHost.GetValueOrDefault(x.Link.Host) < group.MaxPerHost)
            {
                perHost[x.Link.Host] = perHost.GetValueOrDefault(x.Link.Host) + 1;
                chosen.Add(x);
            }
            else
                rest.Add(x);
        }
        chosen = chosen.Take(group.TopN).Concat(rest.Take(Math.Max(group.TopN - chosen.Count, 0)))
                       .OrderByDescending(x => x.R.Score).ToList();

        Json.Save(statePath, state);
        if (chosen.Count == 0)
        {
            log.LogError("[{Group}] ни один конфиг не прошёл проверку — оставляю прошлую подписку", group.Name);
            return;
        }

        var lines = new List<string>();
        var report = new List<NodeReport>();
        for (var i = 0; i < chosen.Count; i++)
        {
            var (v, r) = chosen[i];
            var name = t.CheckSpeed && r.Speed > 0
                ? $"{Flag(r.Country)} {r.Country} {r.Latency}ms {r.Speed:0.##}Mbps #{i + 1}"
                : $"{Flag(r.Country)} {r.Country} {r.Latency}ms #{i + 1}";
            var checks = state[v.Key].Checks;
            lines.Add(v.WithName(name));
            report.Add(new NodeReport
            {
                Rank = i + 1, Name = name, Score = r.Score, Host = v.Host, Port = v.Port, LatencyMs = r.Latency,
                OkRatio = r.OkRatio, SpeedMbps = r.Speed, Sites = r.SitesTotal == 0 ? "" : $"{r.SitesOk}/{r.SitesTotal}", PageMs = r.PageMs, DirectMs = r.DirectMs, ExitIp = r.Ip, Country = r.Country,
                Uptime = $"{checks.Sum()}/{checks.Count}", OriginalName = v.Name,
            });
        }
        var raw = string.Join("\n", lines) + "\n";
        Json.WriteAtomic(Path.Combine(_dir, "sub_raw.txt"), raw);
        Json.WriteAtomic(Path.Combine(_dir, "sub.txt"), Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
        var status = new GroupStatus
        {
            UpdatedTs = now, DurationS = (int)Math.Round(Now() - started), Total = nodes.Count, TcpAlive = alive.Count,
            XrayValid = valid.Count, Working = working.Count, Selected = chosen.Count, Countries = countries, Nodes = report,
        };
        Json.Save(Path.Combine(_dir, "status.json"), status);
        log.LogInformation("[{Group}] готово за {Duration}s: {Working} рабочих, в подписке {Selected}",
                           group.Name, status.DurationS, working.Count, chosen.Count);
    }
}
