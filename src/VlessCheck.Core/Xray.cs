using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace VlessCheck.Core;

public sealed record Entry(VlessLink Link, JsonObject Outbound);

public sealed class Xray(string path, ILogger log)
{
    static string WriteConfig(JsonObject cfg)
    {
        var file = Path.Combine(Path.GetTempPath(), $"vlesscheck-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, cfg.ToJsonString());
        return file;
    }

    async Task<bool> ConfigOk(IReadOnlyList<Entry> entries, CancellationToken ct)
    {
        var file = WriteConfig(XrayConfig.Batch(entries.Select((e, i) => (10000 + i, e.Outbound)).ToList()));
        try
        {
            using var p = Process.Start(new ProcessStartInfo(path, ["run", "-test", "-c", file])
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            var drain = Task.WhenAll(p.StandardOutput.ReadToEndAsync(ct), p.StandardError.ReadToEndAsync(ct));
            await p.WaitForExitAsync(ct);
            await drain;
            return p.ExitCode == 0;
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Один кривой конфиг роняет весь xray — делим пачку пополам, пока не найдём виновных.</summary>
    public async Task<List<Entry>> ValidSubset(IReadOnlyList<Entry> entries, CancellationToken ct)
    {
        if (entries.Count == 0 || await ConfigOk(entries, ct))
            return entries.ToList();
        if (entries.Count == 1)
        {
            log.LogDebug("xray отверг {Link}", entries[0].Link.Raw[..Math.Min(120, entries[0].Link.Raw.Length)]);
            return [];
        }
        var mid = entries.Count / 2;
        var left = await ValidSubset(entries.Take(mid).ToList(), ct);
        left.AddRange(await ValidSubset(entries.Skip(mid).ToList(), ct));
        return left;
    }

    static List<int> FreePorts(int n)
    {
        var listeners = Enumerable.Range(0, n).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToList();
        listeners.ForEach(l => l.Start());
        var ports = listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port).ToList();
        listeners.ForEach(l => l.Stop());
        return ports;
    }

    public async Task<Running> Start(IReadOnlyList<Entry> entries, CancellationToken ct)
    {
        var ports = FreePorts(entries.Count);
        var file = WriteConfig(XrayConfig.Batch(entries.Select((e, i) => (ports[i], e.Outbound)).ToList()));
        var proc = Process.Start(new ProcessStartInfo(path, ["run", "-c", file])
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        proc.OutputDataReceived += (_, _) => { };
        proc.ErrorDataReceived += (_, _) => { };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        var running = new Running(proc, file, ports);

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && !proc.HasExited)
            {
                try
                {
                    using var c = new TcpClient();
                    await c.ConnectAsync(IPAddress.Loopback, ports[^1], ct);
                    return running;
                }
                catch (SocketException)
                {
                    await Task.Delay(100, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            await running.DisposeAsync(); // проверку остановили во время запуска — xray не должен остаться висеть
            throw;
        }
        await running.DisposeAsync();
        throw new InvalidOperationException("xray не запустился");
    }

    public sealed class Running(Process proc, string file, List<int> ports) : IAsyncDisposable
    {
        public IReadOnlyList<int> Ports => ports;

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill();
                    await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception e) when (e is InvalidOperationException or TimeoutException) { }
            proc.Dispose();
            File.Delete(file);
        }
    }
}
