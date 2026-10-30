namespace ServiceLib.Services;

public sealed record MihomoBridgeNode(string Key, int Port, MihomoNodeDocument Document);

// Xray remains the routing engine; each selected Mihomo node has a private loopback
// endpoint. Reference-counted leases let a speed test share an active node safely.
public static class MihomoBridgeService
{
    private sealed class Entry(MihomoBridgeNode node)
    {
        public MihomoBridgeNode Node { get; } = node;
        public ProcessService? Process;
        public string? Directory;
        public int References;
    }

    private static readonly object PortLock = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, Entry> Entries = new();
    private static readonly HashSet<int> ReservedPorts = [];

    public static bool IsNode(ProfileItem? node) =>
        node?.ConfigType == EConfigType.Outbound && node.CoreType == ECoreType.mihomo;

    public static MihomoNodeDocument Read(ProfileItem node)
    {
        try
        {
            var path = File.Exists(node.Address) ? node.Address : Utils.GetConfigPath(node.Address);
            var document = JsonSerializer.Deserialize<MihomoNodeDocument>(File.ReadAllText(path));
            if (document == null || string.IsNullOrWhiteSpace(document.Name) || string.IsNullOrWhiteSpace(document.Yaml))
                throw new FormatException();
            return document;
        }
        catch
        {
            throw new InvalidOperationException("Mihomo 节点配置缺失或无效，请重新更新订阅。");
        }
    }

    public static ProfileItem Resolve(CoreConfigContext context, ProfileItem node)
    {
        var document = Read(node);
        // An unrelated subscription node changing must not reset this node's learning identity.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(MihomoFmt.BridgeConfig(document, 0))));
        Entry entry;
        lock (PortLock)
        {
            if (!Entries.TryGetValue(key, out entry!))
            {
                int port;
                do
                {
                    var socket = new TcpListener(IPAddress.Loopback, 0);
                    socket.Start();
                    port = ((IPEndPoint)socket.LocalEndpoint).Port;
                    socket.Stop();
                } while (!ReservedPorts.Add(port));
                entry = new Entry(new MihomoBridgeNode(key, port, document));
                Entries.Add(key, entry);
            }
        }
        context.MihomoNodes[node.IndexId] = entry.Node;
        context.ProtectCoreTypeList.Add(ECoreType.mihomo);
        if (Utils.IsDomain(document.Server)) context.ProtectDomainList.Add(document.Server);
        return new ProfileItem
        {
            IndexId = node.IndexId, Subid = node.Subid, IsSub = node.IsSub, Remarks = node.Remarks,
            ConfigType = EConfigType.SOCKS, CoreType = ECoreType.Xray,
            Address = Global.Loopback, Port = entry.Node.Port, MuxEnabled = false,
        };
    }

    public static async Task<IDisposable> AcquireAsync(CoreConfigContext context, Action<nint> registerProcess)
    {
        List<Entry> acquired = [];
        await Gate.WaitAsync();
        try
        {
            foreach (var node in context.MihomoNodes.Values.DistinctBy(n => n.Key))
            {
                Entry entry;
                lock (PortLock) entry = Entries[node.Key];
                if (entry.Process == null || entry.Process.HasExited)
                {
                    entry.Process?.Dispose();
                    var core = CoreInfoManager.Instance.GetCoreInfo(ECoreType.mihomo);
                    var exe = CoreInfoManager.Instance.GetCoreExecFile(core, out _);
                    if (string.IsNullOrEmpty(exe))
                        throw new InvalidOperationException("缺少 Mihomo 内核，请先使用“检查更新”安装 Mihomo 内核。");
                    var directory = Utils.GetTempPath("mihomo-bridge-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(directory);
                    entry.Directory = directory;
                    var path = Path.Combine(directory, "config.yaml");
                    try
                    {
                        await File.WriteAllTextAsync(path, MihomoFmt.BridgeConfig(node.Document, node.Port));
                        var process = new ProcessService(exe, $"-d {directory.AppendQuotes()} -f {path.AppendQuotes()}",
                            directory, false, false, null, null);
                        entry.Process = process;
                        await process.StartAsync();
                        registerProcess(process.Handle);
                        await WaitReady(process, node.Port);
                    }
                    catch
                    {
                        Cleanup(entry);
                        throw new InvalidOperationException("Mihomo 节点启动失败，请检查内核版本、节点参数及本地端口。");
                    }
                }
                entry.References++;
                acquired.Add(entry);
            }
            return new Lease(acquired);
        }
        catch
        {
            foreach (var entry in acquired)
                if (--entry.References == 0) Cleanup(entry);
            throw;
        }
        finally { Gate.Release(); }
    }

    private static async Task WaitReady(ProcessService process, int port)
    {
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(100);
            if (process.HasExited) throw new IOException();
            using var socket = new TcpClient();
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromMilliseconds(200));
                await Task.Delay(100);
                if (!process.HasExited) return;
            }
            catch (SocketException) { }
            catch (TimeoutException) { }
        }
        throw new TimeoutException();
    }

    private static void Cleanup(Entry entry)
    {
        entry.Process?.Dispose();
        entry.Process = null;
        if (entry.Directory is { } directory)
        {
            try { Directory.Delete(directory, true); } catch { }
            entry.Directory = null;
        }
    }

    private sealed class Lease(List<Entry> entries) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Gate.Wait();
            try
            {
                foreach (var entry in entries)
                    if (--entry.References == 0) Cleanup(entry);
            }
            finally { Gate.Release(); }
        }
    }
}
