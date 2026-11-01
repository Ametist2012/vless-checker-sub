using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace VlessCheck.Core;

public sealed record VlessLink(string Raw, string Uuid, string Host, int Port, IReadOnlyDictionary<string, string> Params, string Name)
{
    static readonly string[] KeyParams = ["type", "security", "sni", "pbk", "sid", "path", "serviceName", "flow"];

    public string P(string key) => Params.TryGetValue(key, out var v) ? v : "";

    /// <summary>Идентичность сервера без учёта имени: дубли из разных подписок схлопываются.</summary>
    public string Key => $"{Uuid}@{Host}:{Port}|" + string.Join("|", KeyParams.Select(P));

    public string WithName(string name) => Raw.Split('#', 2)[0] + "#" + Uri.EscapeDataString(name);

    public static VlessLink? Parse(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var u) || u.Scheme != "vless"
            || string.IsNullOrEmpty(u.UserInfo) || string.IsNullOrEmpty(u.Host) || u.Port <= 0)
            return null;
        var query = new Dictionary<string, string>();
        foreach (var pair in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            query.TryAdd(Uri.UnescapeDataString(kv[0]), kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "");
        }
        return new VlessLink(link, Uri.UnescapeDataString(u.UserInfo), u.IdnHost, u.Port, query,
                             Uri.UnescapeDataString(u.Fragment.TrimStart('#')));
    }
}

public static partial class Links
{
    // Некоторые панели отдают подписку только «знакомым» клиентам
    const string UserAgent = "v2rayN/7.0";

    [GeneratedRegex("vless://[^\\s\"'<>]+")]
    private static partial Regex LinkRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();

    public static List<string> Extract(string text)
    {
        if (!text.Contains("vless://"))
        {
            try
            {
                var s = Whitespace().Replace(text, "").Replace('-', '+').Replace('_', '/');
                text = Encoding.UTF8.GetString(Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')));
            }
            catch (FormatException)
            {
                return [];
            }
        }
        return LinkRegex().Matches(text).Select(m => m.Value).ToList();
    }

    public static async Task<List<VlessLink>> Gather(IEnumerable<string> sources, ILogger log, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        var batches = await Task.WhenAll(sources.Select(async src =>
        {
            try
            {
                var found = Extract(await http.GetStringAsync(src, ct));
                log.LogInformation("источник {Source}: {Count} vless", src, found.Count);
                return found;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                log.LogWarning("источник {Source} недоступен: {Error}", src, e.Message);
                return [];
            }
        }));

        var seen = new HashSet<string>();
        var result = new List<VlessLink>();
        foreach (var link in batches.SelectMany(b => b))
            if (VlessLink.Parse(link) is { } v && seen.Add(v.Key))
                result.Add(v);
        return result;
    }
}
