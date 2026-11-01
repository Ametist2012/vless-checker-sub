using VlessCheck.Checker;
using VlessCheck.Core;

// checker              — проверка по расписанию (systemd-сервис)
// checker once [group] — один цикл и выход
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
builder.Services.AddSingleton(new Store(Environment.GetEnvironmentVariable("VLESSCHECK_DATA") ?? "data"));
builder.Services.AddSingleton<CheckRunner>();

if (args.FirstOrDefault() == "probe" && args.Length > 1)
{
    // диагностика одной ссылки: checker probe "vless://..."
    var link = VlessLink.Parse(args[1]) ?? throw new ArgumentException("не vless-ссылка");
    var t = new Settings();
    using var host = builder.Build();
    var log = host.Services.GetRequiredService<ILogger<CheckRunner>>();
    var xray = new Xray(Environment.GetEnvironmentVariable("VLESSCHECK_XRAY") ?? "xray", log);
    await using var running = await xray.Start([new Entry(link, XrayConfig.Outbound(link))], CancellationToken.None);
    foreach (var url in new[] { t.LatencyUrl, t.LatencyUrl, t.TraceUrl, t.SpeedUrl })
    {
        try
        {
            var r = await Probe.HttpGet(url, running.Ports[0], url == t.SpeedUrl ? t.SpeedTimeout : t.Timeout, CancellationToken.None);
            Console.WriteLine($"{url}: {r.Status}, {r.Body.Length} B, ttfb {r.Ttfb * 1000:0} ms, всего {r.Total * 1000:0} ms");
        }
        catch (Exception e)
        {
            Console.WriteLine($"{url}: {e.GetType().Name}: {e.Message} {e.InnerException?.Message}");
        }
    }
    return;
}

if (args.FirstOrDefault() == "once")
{
    using var host = builder.Build();
    await host.Services.GetRequiredService<CheckRunner>().RunGroups(args.ElementAtOrDefault(1) ?? "*", CancellationToken.None);
    return;
}

builder.Services.AddHostedService<CheckerWorker>();
builder.Build().Run();
