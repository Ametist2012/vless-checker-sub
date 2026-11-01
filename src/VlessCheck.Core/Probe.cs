using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace VlessCheck.Core;

public sealed class ProbeResult
{
    public int Latency { get; set; }
    public double OkRatio { get; set; }
    public string Ip { get; set; } = "";
    public string Country { get; set; } = "";
    public double Speed { get; set; }
    public int SitesOk { get; set; }
    public int SitesTotal { get; set; }
    public int PageMs { get; set; }
    public int DirectMs { get; set; }
    public double Score { get; set; }
}

/// <summary>Реальная проверка: ходим в интернет через локальный http-вход xray.</summary>
public static class Probe
{
    public sealed record Response(int Status, byte[] Body, double Total, double Ttfb);

    /// <summary>GET через прокси на 127.0.0.1:port (null — напрямую). Каждый вызов — новое соединение,
    /// поэтому задержка включает рукопожатие с сервером, как «реальная задержка» в клиентах.</summary>
    public static async Task<Response> HttpGet(string url, int? proxyPort, double timeoutSec, CancellationToken ct, long maxBytes = 0,
        bool browser = false)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        using var handler = new SocketsHttpHandler
        {
            UseProxy = proxyPort is not null,
            Proxy = proxyPort is { } p ? new WebProxy($"http://127.0.0.1:{p}") : null,
            AllowAutoRedirect = browser,
            AutomaticDecompression = browser ? DecompressionMethods.All : DecompressionMethods.None,
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(browser
            ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36"
            : "Mozilla/5.0");
        if (browser)
            req.Headers.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.8");

        var sw = Stopwatch.StartNew();
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var ttfb = sw.Elapsed.TotalSeconds;
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var body = new MemoryStream();
        var buf = new byte[65536];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buf, cts.Token)) > 0)
            {
                body.Write(buf, 0, n);
                if (maxBytes > 0 && body.Length >= maxBytes)
                    break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && body.Length > 0)
        {
            // время вышло посреди скачивания — для замера скорости берём то, что успели
        }
        return new Response((int)resp.StatusCode, body.ToArray(), sw.Elapsed.TotalSeconds, ttfb);
    }

    public static Dictionary<string, string> ParseTrace(byte[] body) =>
        Encoding.UTF8.GetString(body).Split('\n')
            .Select(l => l.Split('=', 2)).Where(kv => kv.Length == 2)
            .GroupBy(kv => kv[0]).ToDictionary(g => g.Key, g => g.First()[1].Trim());

    /// <summary>Прямой пинг: время TCP-подключения к серверу без прокси (мс), null — порт закрыт.</summary>
    public static async Task<double?> TcpPing(VlessLink v, double timeoutSec, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        try
        {
            // DNS отдельно, чтобы в пинг попало только подключение
            var addresses = IPAddress.TryParse(v.Host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(v.Host, cts.Token);
            using var c = new TcpClient(addresses[0].AddressFamily);
            var sw = Stopwatch.StartNew();
            await c.ConnectAsync(addresses[0], v.Port, cts.Token);
            return sw.Elapsed.TotalMilliseconds;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or IndexOutOfRangeException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Пинг через подключение (несколько запросов generate_204, если включён) + проверка внешнего IP через
    /// cloudflare trace — она выполняется всегда: это доказательство, что трафик идёт через сервер, и страна для фильтра.</summary>
    public static async Task<ProbeResult?> Latency(int port, Settings t, string? ownIp, CancellationToken ct)
    {
        var lats = new List<double>();
        for (var i = 0; t.CheckProxyPing && i < t.Attempts; i++)
        {
            try
            {
                var r = await HttpGet(t.LatencyUrl, port, t.Timeout, ct);
                if (r.Status is 200 or 204)
                    lats.Add(r.Total * 1000);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            if (i == 0 && lats.Count == 0)
                return null; // первая попытка не прошла — не тратим время
        }
        if (t.CheckProxyPing && lats.Count == 0)
            return null;

        Dictionary<string, string> trace;
        try
        {
            var r = await HttpGet(t.TraceUrl, port, t.Timeout, ct);
            if (r.Status != 200)
                return null;
            trace = ParseTrace(r.Body);
            if (!t.CheckProxyPing)
                lats.Add(r.Total * 1000); // без пинга задержкой считаем время этого запроса
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        if (!trace.TryGetValue("ip", out var ip) || ip == "" || ip == ownIp)
            return null;
        lats.Sort();
        var median = lats.Count % 2 == 1 ? lats[lats.Count / 2] : (lats[lats.Count / 2 - 1] + lats[lats.Count / 2]) / 2;
        return new ProbeResult
        {
            Latency = (int)Math.Round(median), OkRatio = t.CheckProxyPing ? (double)lats.Count / t.Attempts : 1,
            Ip = ip, Country = trace.GetValueOrDefault("loc", ""),
        };
    }

    /// <summary>Загрузка настоящих страниц, как в браузере: сколько открылось и среднее время полной загрузки HTML.
    /// Честнее speedtest — его на части серверов блокируют, хотя сайты открываются нормально.</summary>
    public static async Task<(int Ok, int AvgMs)> Sites(int port, Settings t, CancellationToken ct)
    {
        var times = new List<double>();
        foreach (var url in t.SiteUrls)
        {
            try
            {
                var r = await HttpGet(url, port, t.SiteTimeout, ct, browser: true);
                if (r.Status is >= 200 and < 400 && r.Body.Length > 0)
                    times.Add(r.Total * 1000);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }
        return (times.Count, times.Count == 0 ? 0 : (int)Math.Round(times.Average()));
    }

    /// <summary>Мбит/с по скачиванию файла, без учёта времени установки соединения.</summary>
    public static async Task<double> Speed(int port, Settings t, CancellationToken ct)
    {
        try
        {
            var r = await HttpGet(t.SpeedUrl, port, t.SpeedTimeout, ct);
            var dt = Math.Max(r.Total - r.Ttfb, 0.05);
            return Math.Round(r.Body.Length * 8 / dt / 1e6, 2);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return 0;
        }
    }

    /// <summary>Прогоняет fn(port) для каждого конфига пачками по batchSize в одном xray;
    /// результаты в том же порядке, что entries.</summary>
    public static async Task<T?[]> RunBatches<T>(Xray xray, IReadOnlyList<Entry> entries, int batchSize, int workers,
        Func<int, Task<T?>> fn, Action<int, int>? onProgress, ILogger log, CancellationToken ct)
    {
        var results = new T?[entries.Count];
        for (var start = 0; start < entries.Count; start += batchSize)
        {
            var chunk = entries.Skip(start).Take(batchSize).ToList();
            try
            {
                await using var running = await xray.Start(chunk, ct);
                var offset = start;
                await Parallel.ForEachAsync(Enumerable.Range(0, chunk.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
                    async (i, _) => results[offset + i] = await fn(running.Ports[i]));
            }
            catch (InvalidOperationException e)
            {
                log.LogWarning("пачка {Start} пропущена: {Error}", start, e.Message);
            }
            var done = Math.Min(start + batchSize, entries.Count);
            log.LogInformation("проверено {Done}/{Total}", done, entries.Count);
            onProgress?.Invoke(done, entries.Count);
        }
        return results;
    }
}
