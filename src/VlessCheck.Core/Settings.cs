using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VlessCheck.Core;

/// <summary>Все настройки сервиса. Хранятся одним JSON в SQLite, в API — в snake_case.</summary>
public sealed class Settings
{
    public bool Enabled { get; set; } = true; // выключено: проверки стоят, подписки не отдаются
    public string PanelPath { get; set; } = ""; // секретный сегмент адреса панели: …/checker/<PanelPath>/
    public int IntervalMinutes { get; set; } = 30;
    public List<string> ExcludeCountries { get; set; } = ["JP", "KR", "US"];
    public bool HappOnly { get; set; } = true; // отдавать подписку только приложению Happ (по User-Agent)
    public string RoutingLink { get; set; } = ""; // happ://routing/... — одна маршрутизация на все группы
    public bool RoutingInSubscription { get; set; } = true;

    public string LatencyUrl { get; set; } = "https://www.gstatic.com/generate_204";
    public string TraceUrl { get; set; } = "https://www.cloudflare.com/cdn-cgi/trace";
    public List<string> SiteUrls { get; set; } = ["https://www.google.com/", "https://www.wikipedia.org/", "https://github.com/"];
    public double SiteTimeout { get; set; } = 10;
    // способы проверки (60 баллов делятся поровну между включёнными); проверка IP выхода выполняется всегда
    public bool CheckDirectPing { get; set; }          // TCP-подключение к серверу напрямую, без прокси
    public bool CheckProxyPing { get; set; } = true;   // «реальная задержка» через сам конфиг
    public bool CheckSites { get; set; } = true;       // загрузка настоящих сайтов
    public bool CheckSpeed { get; set; }               // speedtest — на части серверов его блокируют
    public double SpeedCapMbps { get; set; } = 20;
    public int SpeedBytes { get; set; } = 3_000_000;
    public int Attempts { get; set; } = 3;
    public double Timeout { get; set; } = 8;
    public double TcpTimeout { get; set; } = 3;
    public int BatchSize { get; set; } = 25;
    public int Workers { get; set; } = 12;
    public int SpeedTestTop { get; set; } = 150;
    public int SpeedWorkers { get; set; } = 2;
    public double SpeedTimeout { get; set; } = 12;

    // служебные, через PATCH /api/settings не меняются
    public string SubToken { get; set; } = ""; // общий токен старой версии — только для переноса в группы
    public Dictionary<string, HappLink> HappLinks { get; set; } = [];

    [JsonIgnore]
    public string SpeedUrl => $"https://speed.cloudflare.com/__down?bytes={SpeedBytes}";

    public static readonly HashSet<string> ReadOnlyKeys = ["sub_token", "happ_links"];

    public void Validate()
    {
        double[] positive = [IntervalMinutes, SiteTimeout, SpeedBytes, Attempts, Timeout, TcpTimeout, BatchSize, Workers,
                             SpeedTestTop, SpeedWorkers, SpeedTimeout, SpeedCapMbps];
        if (positive.Any(x => x <= 0))
            throw new ArgumentException("числовые настройки должны быть больше нуля");
        if (!Uri.IsWellFormedUriString(LatencyUrl, UriKind.Absolute) || !Uri.IsWellFormedUriString(TraceUrl, UriKind.Absolute))
            throw new ArgumentException("некорректный адрес проверки");
        PanelPath = PanelPath.Trim().Trim('/');
        if (!Regex.IsMatch(PanelPath, "^[A-Za-z0-9_-]{4,64}$") || PanelPath is "sub" or "api")
            throw new ArgumentException("адрес панели: 4–64 символа, латиница, цифры, - и _");
        if (!CheckProxyPing && !CheckSites)
            throw new ArgumentException("включите «пинг через подключение» или «загрузку сайтов» — иначе не проверить, что через конфиг идёт трафик");
        SiteUrls = SiteUrls.Select(u => u.Trim()).Where(u => u != "").Distinct().ToList();
        if (SiteUrls.Count is 0 or > 10)
            throw new ArgumentException("сайтов для проверки: от 1 до 10");
        if (SiteUrls.Any(u => !(Uri.TryCreate(u, UriKind.Absolute, out var x) && x.Scheme is "http" or "https")))
            throw new ArgumentException("сайты для проверки: только ссылки http(s)://");
        RoutingLink = RoutingLink.Trim();
        if (RoutingLink != "")
            HappRouting.Parse(RoutingLink);
        ExcludeCountries = ExcludeCountries
            .Where(c => c.Length == 2 && c.All(char.IsAsciiLetter))
            .Select(c => c.ToUpperInvariant()).Distinct().Order().ToList();
    }
}

public sealed record HappLink(string Url, string Link);

public sealed class Group
{
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public int TopN { get; set; } = 55;
    public int MaxPerHost { get; set; } = 2;
    public string Token { get; set; } = "";
    public List<Subscription> Subscriptions { get; set; } = [];
}

public sealed record Subscription(long Id, string Url, double Created);
