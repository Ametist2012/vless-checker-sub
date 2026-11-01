using System.Text.Json.Nodes;

namespace VlessCheck.Core;

/// <summary>Сборка конфигурации xray: много http-входов, у каждого свой vless-выход.</summary>
public static class XrayConfig
{
    static JsonArray Split(string s) => new(s.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => (JsonNode)x).ToArray());

    static string Or(string value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;

    public static JsonObject StreamSettings(VlessLink v)
    {
        var net = Or(v.P("type"), "tcp").ToLowerInvariant() switch { "raw" => "tcp", "splithttp" => "xhttp", var n => n };
        var sec = Or(v.P("security"), "none").ToLowerInvariant();
        var path = Or(v.P("path"), "/");
        var host = v.P("host");
        var ss = new JsonObject { ["network"] = net, ["security"] = sec };

        switch (net)
        {
            case "tcp":
                if (v.P("headerType") == "http")
                {
                    var paths = Split(path);
                    ss["tcpSettings"] = new JsonObject
                    {
                        ["header"] = new JsonObject
                        {
                            ["type"] = "http",
                            ["request"] = new JsonObject
                            {
                                ["path"] = paths.Count > 0 ? paths : new JsonArray("/"),
                                ["headers"] = new JsonObject { ["Host"] = Split(host) },
                            },
                        },
                    };
                }
                break;
            case "ws":
                ss["wsSettings"] = new JsonObject { ["path"] = path, ["host"] = host };
                break;
            case "grpc":
                ss["grpcSettings"] = new JsonObject
                {
                    ["serviceName"] = v.P("serviceName"),
                    ["multiMode"] = v.P("mode") == "multi",
                    ["authority"] = v.P("authority"),
                };
                break;
            case "httpupgrade":
                ss["httpupgradeSettings"] = new JsonObject { ["path"] = path, ["host"] = host };
                break;
            case "xhttp":
                var x = new JsonObject { ["path"] = path, ["host"] = host, ["mode"] = Or(v.P("mode"), "auto") };
                if (v.P("extra") != "")
                    x["extra"] = JsonNode.Parse(v.P("extra"));
                ss["xhttpSettings"] = x;
                break;
            case "kcp":
                ss["kcpSettings"] = new JsonObject
                {
                    ["header"] = new JsonObject { ["type"] = Or(v.P("headerType"), "none") },
                    ["seed"] = v.P("seed"),
                };
                break;
            default:
                throw new ArgumentException($"неподдерживаемый транспорт {net}");
        }

        var sni = Or(v.P("sni"), v.P("peer"));
        var fp = Or(v.P("fp"), "chrome");
        switch (sec)
        {
            case "tls":
                var tls = new JsonObject { ["serverName"] = Or(sni, Or(host, v.Host)), ["fingerprint"] = fp };
                if (v.P("alpn") != "")
                    tls["alpn"] = Split(v.P("alpn"));
                ss["tlsSettings"] = tls;
                break;
            case "reality":
                if (v.P("pbk") == "")
                    throw new ArgumentException("reality без pbk");
                ss["realitySettings"] = new JsonObject
                {
                    ["serverName"] = sni, ["fingerprint"] = fp, ["publicKey"] = v.P("pbk"),
                    ["shortId"] = v.P("sid"), ["spiderX"] = v.P("spx"),
                };
                break;
            case "none":
                break;
            default:
                throw new ArgumentException($"неподдерживаемый security {sec}");
        }
        return ss;
    }

    public static JsonObject Outbound(VlessLink v)
    {
        var user = new JsonObject { ["id"] = v.Uuid, ["encryption"] = Or(v.P("encryption"), "none") };
        if (v.P("flow") != "")
            user["flow"] = v.P("flow");
        return new JsonObject
        {
            ["protocol"] = "vless",
            ["settings"] = new JsonObject
            {
                ["vnext"] = new JsonArray(new JsonObject
                {
                    ["address"] = v.Host, ["port"] = v.Port, ["users"] = new JsonArray(user),
                }),
            },
            ["streamSettings"] = StreamSettings(v),
        };
    }

    /// <summary>Трафик с каждого локального порта уходит строго в свой outbound.</summary>
    public static JsonObject Batch(IReadOnlyList<(int Port, JsonObject Outbound)> items)
    {
        var inbounds = new JsonArray();
        var outbounds = new JsonArray(new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" });
        var rules = new JsonArray();
        for (var i = 0; i < items.Count; i++)
        {
            inbounds.Add(new JsonObject { ["tag"] = $"in{i}", ["listen"] = "127.0.0.1", ["port"] = items[i].Port, ["protocol"] = "http" });
            var ob = (JsonObject)items[i].Outbound.DeepClone();
            ob["tag"] = $"out{i}";
            outbounds.Add(ob);
            rules.Add(new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray($"in{i}"), ["outboundTag"] = $"out{i}" });
        }
        return new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "none" },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["routing"] = new JsonObject { ["rules"] = rules },
        };
    }
}
