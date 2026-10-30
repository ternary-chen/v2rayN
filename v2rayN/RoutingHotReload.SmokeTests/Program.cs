using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AdaptiveRouting;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Handler.Builder;
using ServiceLib.Helper;
using ServiceLib.Manager;
using ServiceLib.Models.Entities;
using ServiceLib.Services;

static class Program
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    static async Task Main(string[] args)
    {
        Check(args.Length == 1 && File.Exists(args[0]), "Pass a local Xray executable; no download is performed.");
        Check(AppContext.BaseDirectory.Contains("RoutingHotReload.SmokeTests"), "Must run in isolated build directory");
        Environment.SetEnvironmentVariable(Global.LocalAppData, "0");
        Check(Path.GetFullPath(Utils.StartupPath()).TrimEnd(Path.DirectorySeparatorChar) == Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), "Unexpected storage path");
        Check(AppManager.Instance.InitApp(), "Fixture init failed");
        var binary = Utils.GetBinPath(Utils.GetExeName("xray"), "xray");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.Copy(args[0], binary, true);
        await using var normalServer = new MarkerServer((byte)'A', true);
        await using var dedicatedServer = new MarkerServer((byte)'B', true);
        await using var directServer = new MarkerServer((byte)'D', false);
        // Learning deliberately accepts only web ports; bind an unused loopback-only web endpoint exclusively.
        await using var learningWebServer = new MarkerServer((byte)'L', false, IPAddress.Parse("127.0.0.2"), 80);
        var config = AppManager.Instance.Config;
        var id = Guid.NewGuid().ToString("N");
        ProfileItem Node(string name, int port) => new() { IndexId = id + name, Remarks = name, ConfigType = EConfigType.SOCKS, CoreType = ECoreType.Xray, Address = "127.0.0.1", Port = port };
        var normal = Node("default", normalServer.Port);
        var dedicated = Node("dedicated", dedicatedServer.Port);
        var db = SQLiteHelper.Instance;
        await db.InsertAllAsync(new[] { normal, dedicated });
        config.IndexId = normal.IndexId;
        config.TunModeItem.EnableTun = false;
        config.LimitedLearning.Enabled = true;
        config.LimitedLearning.ListenerPort = FreePort();
        config.LimitedLearning.ProxyPort = FreePort();
        config.LimitedLearning.DirectProcesses = [];
        config.AiPolicy = new() { Enabled = false, AiProfileId = dedicated.IndexId };
        config.ManualRules = [new() { Kind = "domain", Value = "localhost", Target = "proxy" }];
        config.Inbound[0].LocalPort = FreePort();
        config.Inbound[0].SniffingEnabled = false;
        config.GuiItem.EnableStatistics = false;
        config.GuiItem.DisplayRealTimeSpeed = false;
        var route = new RoutingItem { Id = id, Remarks = "isolated-hot-reload", IsActive = false, DomainStrategy = "AsIs", RuleSet = "[{\"OutboundTag\":\"proxy\",\"Domain\":[\"full:localhost\"]},{\"OutboundTag\":\"direct\",\"Network\":\"tcp,udp\"}]" };
        await db.InsertAsync(route);
        await ConfigHandler.SetDefaultRouting(config, route);
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var manager = CoreManager.Instance;
        await manager.Init(config, (_, message) => { logs.Enqueue(message); return Task.CompletedTask; });
        Process[] TestCores() => Process.GetProcessesByName("xray").Where(p =>
        {
            try { return string.Equals(p.MainModule?.FileName, binary, StringComparison.OrdinalIgnoreCase); }
            catch { p.Dispose(); return false; }
        }).ToArray();
        try
        {
            var built = await CoreConfigContextBuilder.BuildAll(config, normal);
            Check(built.Success, string.Join(";", built.CombinedValidatorResult.Errors));
            await manager.LoadCore(built.MainResult.Context, null);
            var processes = TestCores();
            Check(processes.Length == 1, "Expected exactly one isolated Xray process: " + string.Join("\n", logs));
            using var core = processes[0];
            var pid = core.Id;
            using var oldA = await Open(config.Inbound[0].LocalPort, directServer.Port);
            await Ping(oldA, (byte)'A');
            using var learnedStream = await Open(config.LimitedLearning.ListenerPort, learningWebServer.Port, "127.0.0.2");
            await Ping(learnedStream, (byte)'L');
            var saved = await TrafficPolicyService.SaveAndApplyAsync(config, false, dedicated.IndexId, normal.IndexId,
                [new() { Kind = "domain", Value = "localhost", Target = "ai-policy" }], []);
            Check(!saved && !core.HasExited, "Rule-only save requested reload or stopped core");
            using var oldB = await Open(config.Inbound[0].LocalPort, directServer.Port);
            await Ping(oldB, (byte)'B'); await Ping(oldA, (byte)'A');
            await TrafficPolicyService.SaveAndApplyAsync(config, false, dedicated.IndexId, normal.IndexId,
                [new() { Kind = "domain", Value = "localhost", Target = "direct" }], []);
            using var oldD = await Open(config.Inbound[0].LocalPort, directServer.Port);
            await Ping(oldD, (byte)'D'); await Ping(oldB, (byte)'B'); await Ping(oldA, (byte)'A');
            Check(!core.HasExited && core.Id == pid, "PID changed");
            Console.WriteLine($"PASS Live Xray PID {pid} unchanged; existing A/B streams survive default -> dedicated -> direct; new connections use B/D");

            var diskBefore = await File.ReadAllBytesAsync(Utils.GetConfigPath("guiNConfig.json"));
            var runtimeBefore = await File.ReadAllTextAsync(Utils.GetBinConfigPath(Global.CoreConfigFileName));
            try
            {
                await TrafficPolicyService.SaveAndApplyAsync(config, false, dedicated.IndexId, normal.IndexId,
                    [new() { Kind = "geosite", Value = "nonexistent-hot-reload-fixture", Target = "proxy" }], []);
                throw new Exception("Invalid Geo rule was accepted");
            }
            catch (InvalidOperationException) { }
            Check((await File.ReadAllBytesAsync(Utils.GetConfigPath("guiNConfig.json"))).SequenceEqual(diskBefore), "Rejected rule rewrote saved config");
            using var stillD = await Open(config.Inbound[0].LocalPort, directServer.Port);
            await Ping(stillD, (byte)'D'); await Ping(oldA, (byte)'A');
            Check(!core.HasExited, "Rejected rule stopped core");
            Console.WriteLine("PASS Rejected rule preserves runtime, saved config and established connection");

            var afterNode = JsonNode.Parse(runtimeBefore)!;
            var directRule = afterNode["routing"]!["rules"]!.AsArray().First(x => x?["ruleTag"]?.ToString() == "manual-rule-1")!;
            directRule["outboundTag"] = "proxy";
            var changed = XrayRoutingHotReload.Prepare(afterNode.ToJsonString(), AppManager.Instance.RoutingApiPort);
            try
            {
                await XrayRoutingHotReload.ApplyAsync(binary, Utils.GetBinPath(""), Utils.GetTempPath(), runtimeBefore, changed,
                    () => !core.HasExited, () => throw new IOException("simulated persistence failure"));
                throw new Exception("Persist failure was swallowed");
            }
            catch (InvalidOperationException) { }
            using var rolledBack = await Open(config.Inbound[0].LocalPort, directServer.Port);
            await Ping(rolledBack, (byte)'D'); await Ping(oldA, (byte)'A');
            Console.WriteLine("PASS Persistence failure rolls runtime rules back without breaking existing connections");

            // Same tags/outbounds but changed match content must still be detected via revision stamp.
            await TrafficPolicyService.SaveAndApplyAsync(config, false, dedicated.IndexId, normal.IndexId,
                [new() { Kind = "domain", Value = "different.example", Target = "direct" }], []);
            using var nowA = await Open(config.Inbound[0].LocalPort, directServer.Port);
            await Ping(nowA, (byte)'A'); await Ping(oldD, (byte)'D');
            // Disable/remove rules and reorder overlapping rules using the same public save path.
            await TrafficPolicyService.SaveAndApplyAsync(config, false, dedicated.IndexId, normal.IndexId,
                [new() { Kind = "domain", Value = "localhost", Target = "ai-policy", Enabled = false }], []);
            using var disabledA = await Open(config.Inbound[0].LocalPort, directServer.Port); await Ping(disabledA, (byte)'A');
            var priority = new List<ManualRouteRule> { new() { Kind = "domain", Value = "localhost", Target = "direct" }, new() { Kind = "domain", Value = "localhost", Target = "ai-policy" } };
            await TrafficPolicyService.SaveAndApplyAsync(config, false, dedicated.IndexId, normal.IndexId, priority, []);
            using var firstD = await Open(config.Inbound[0].LocalPort, directServer.Port); await Ping(firstD, (byte)'D');
            priority.Reverse();
            await TrafficPolicyService.SaveAndApplyAsync(config, false, dedicated.IndexId, normal.IndexId, priority, []);
            using var firstB = await Open(config.Inbound[0].LocalPort, directServer.Port); await Ping(firstB, (byte)'B');
            await TrafficPolicyService.SaveAndApplyAsync(config, false, dedicated.IndexId, normal.IndexId, [], []);
            using var removedA = await Open(config.Inbound[0].LocalPort, directServer.Port); await Ping(removedA, (byte)'A');
            await Ping(oldA, (byte)'A'); await Ping(oldB, (byte)'B'); await Ping(oldD, (byte)'D');
            Console.WriteLine("PASS Match-content edit, disable, reorder and delete affect new connections only");

            try
            {
                await TrafficPolicyService.SaveAndApplyAsync(config, false, normal.IndexId, normal.IndexId, [], []);
                throw new Exception("Node change allowed through rule-only button");
            }
            catch (InvalidOperationException) { }
            Check(!core.HasExited && core.Id == pid, "Node rejection restarted core");
            await Ping(oldA, (byte)'A');
            Console.WriteLine("PASS Rule-only action rejects node changes, never falls back to restarting");
            await Ping(learnedStream, (byte)'L');
            Check(LimitedLearningRuntime.Running, "Learning runtime was stopped");
            Console.WriteLine("PASS Existing learning-proxy stream also survives all rule updates and rollback");
        }
        catch { Console.Error.WriteLine(string.Join("\n", logs)); throw; }
        finally { await manager.CoreStop(); await db.DisposeDbConnectionAsync(); }
    }

    static async Task<TcpClient> Open(int proxyPort, int destinationPort, string destinationHost = "localhost")
    {
        var tcp = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            var stream = tcp.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
            var reply = new byte[2]; await stream.ReadExactlyAsync(reply, timeout.Token);
            Check(reply[1] == 0, "SOCKS auth failed");
            var host = Encoding.ASCII.GetBytes(destinationHost);
            await stream.WriteAsync(new byte[] { 5, 1, 0, 3, (byte)host.Length }.Concat(host).Concat(new byte[] { (byte)(destinationPort >> 8), (byte)(destinationPort & 255) }).ToArray(), timeout.Token);
            var header = new byte[4]; await stream.ReadExactlyAsync(header, timeout.Token);
            Check(header[1] == 0, "SOCKS connect rejected");
            var remaining = header[3] == 1 ? 6 : header[3] == 4 ? 18 : throw new Exception("Unexpected bind address");
            await stream.ReadExactlyAsync(new byte[remaining], timeout.Token);
            return tcp;
        }
        catch { tcp.Dispose(); throw; }
    }

    static async Task Ping(TcpClient tcp, byte expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stream = tcp.GetStream(); await stream.WriteAsync(new byte[] { 77 }, timeout.Token);
        var result = new byte[2]; await stream.ReadExactlyAsync(result, timeout.Token);
        Check(result[0] == expected && result[1] == 77, $"Wrong route/stream: expected {(char)expected}, got {(char)result[0]}");
    }
}

sealed class MarkerServer : IAsyncDisposable
{
    readonly TcpListener _listener;
    readonly CancellationTokenSource _stop = new();
    readonly Task _accept;
    readonly List<Task> _clients = [];
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public MarkerServer(byte marker, bool socks, IPAddress? address = null, int port = 0)
    {
        _listener = new(address ?? IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        _listener.Start();
        _accept = Task.Run(async () =>
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    lock (_clients) _clients.Add(Serve(client, marker, socks));
                }
            }
            catch (OperationCanceledException) { }
        });
    }
    async Task Serve(TcpClient client, byte marker, bool socks)
    {
        using (client)
        try
        {
            var stream = client.GetStream(); var ct = _stop.Token;
            if (socks)
            {
                var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, ct);
                await stream.ReadExactlyAsync(new byte[greeting[1]], ct);
                await stream.WriteAsync(new byte[] { 5, 0 }, ct);
                var header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
                var length = header[3] switch { 1 => 4, 4 => 16, 3 => 0, _ => throw new IOException() };
                if (length == 0) { var size = new byte[1]; await stream.ReadExactlyAsync(size, ct); length = size[0]; }
                await stream.ReadExactlyAsync(new byte[length + 2], ct);
                await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, ct);
            }
            var b = new byte[1];
            while (await stream.ReadAsync(b, ct) == 1) await stream.WriteAsync(new byte[] { marker, b[0] }, ct);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException) { }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop(); await _accept;
        Task[] clients; lock (_clients) clients = _clients.ToArray();
        await Task.WhenAll(clients); _stop.Dispose();
    }
}
