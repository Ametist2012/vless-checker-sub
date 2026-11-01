using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VlessCheck.Core;

/// <summary>Ссылка маршрутизации Happ: happ://routing/add|onadd/{base64 JSON-профиля} или happ://routing/off.</summary>
public static partial class HappRouting
{
    [GeneratedRegex(@"^happ://routing/(?:(?<mode>add|onadd)/(?<data>[A-Za-z0-9+/_=-]+)|(?<mode>off))$")]
    private static partial Regex LinkRegex();

    // в заголовке длинный профиль может упереться в буферы nginx; в теле подписки ограничений нет
    public const int MaxHeaderLength = 3000;

    /// <summary>Проверяет ссылку и возвращает профиль (null для happ://routing/off).</summary>
    public static JsonObject? Parse(string link)
    {
        var m = LinkRegex().Match(link);
        if (!m.Success)
            throw new ArgumentException("ожидается ссылка вида happ://routing/onadd/… , happ://routing/add/… или happ://routing/off");
        if (m.Groups["mode"].Value == "off")
            return null;
        var data = m.Groups["data"].Value.Replace('-', '+').Replace('_', '/').TrimEnd('=');
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(data.PadRight(data.Length + (4 - data.Length % 4) % 4, '=')));
            return JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("внутри ссылки не JSON-объект");
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            throw new ArgumentException("ссылка повреждена: внутри должен быть JSON-профиль в base64");
        }
    }
}
