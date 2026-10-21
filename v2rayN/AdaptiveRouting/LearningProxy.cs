using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace AdaptiveRouting;

/// <summary>Loopback SOCKS5 CONNECT server. It probes only unknown web hosts and never
/// decrypts/replays the application's TLS or HTTP request. UDP is deliberately unsupported.</summary>
public sealed class LearningProxy : IAsyncDisposable
{
    private readonly LearningOptions _options;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(128);
    private readonly ConcurrentDictionary<int, Task> _tasks = new();
    private readonly LearningDecision _decision;
    private Task? _accept;
    private int _id;
    public LearningStore Store { get; }
    public event Action<string>? Message;

    public LearningProxy(LearningOptions options, string storageFile, string routeIdentity)
    {
        options.Validate();
        _options = options;
        Store = new(storageFile, options.MaxEntries);
        _listener = new(IPAddress.Loopback, options.ListenerPort);
        _decision = new(Store, options, routeIdentity, ProbeAsync);
        _decision.DecisionMade += text => Message?.Invoke(text);
    }

    public void Start()
    {
        _listener.Start(128);
        _accept = AcceptAsync();
    }

    public void Forget(LearnedHost entry) => _decision.Forget(entry.Host, entry.Port, entry.RouteIdentity);

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _slots.WaitAsync(_stop.Token);
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { _slots.Release(); throw; }
                var id = Interlocked.Increment(ref _id);
                var task = ServeAsync(client);
                _tasks[id] = task;
                _ = task.ContinueWith(t => { _tasks.TryRemove(id, out var ignored); _slots.Release(); }, TaskScheduler.Default);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            var stream = client.GetStream();
            var established = false;
            string? destination = null;
            var phase = "SOCKS 协商";
            try
            {
                using var setup = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                setup.CancelAfter(TimeSpan.FromSeconds(45));
                var ct = setup.Token;
                var hello = new byte[2]; await stream.ReadExactlyAsync(hello, ct);
                if (hello[0] != 5 || hello[1] == 0) return;
                var methods = new byte[hello[1]]; await stream.ReadExactlyAsync(methods, ct);
                if (!methods.Contains((byte)0)) { await stream.WriteAsync(new byte[] { 5, 255 }, ct); return; }
                await stream.WriteAsync(new byte[] { 5, 0 }, ct);
                var header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
                if (header[0] != 5 || header[1] != 1 || header[2] != 0) throw new IOException("CONNECT required");
                var host = LearningStore.NormalizeHost(await ReadHostAsync(stream, header[3], ct));
                var portBytes = new byte[2]; await stream.ReadExactlyAsync(portBytes, ct);
                var port = (portBytes[0] << 8) | portBytes[1];
                if (port is not (80 or 443)) throw new IOException("Learning listener accepts web ports only");
                destination = $"{host}:{port}";
                phase = "本地地址检查";
                // Never send private/loopback destinations to the remote proxy, even after local DNS resolves a public-looking name.
                var addresses = await ResolveLocalAsync(host, ct);
                var isPrivate = addresses.Any(IsPrivate);
                if (isPrivate) Message?.Invoke($"{destination}: 解析到本地或私有地址，选择直连，不参与学习。");
                phase = "路由检测";
                var route = isPrivate ? ConnectionRoute.Direct : await _decision.DecideAsync(host, port, ct);
                TcpClient upstream;
                phase = $"{(route == ConnectionRoute.Direct ? "直连" : "代理")}通道建连";
                try { upstream = await OpenAsync(host, port, route, ct); }
                catch
                {
                    if (route == ConnectionRoute.Proxy)
                        Message?.Invoke($"{destination}: 代理通道建连失败，保留之前的确认次数和有效期；本次不增加次数。");
                    throw;
                }
                using (upstream)
                {
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, ct);
                    established = true;
                    Message?.Invoke($"{destination}: 已建立{(route == ConnectionRoute.Direct ? "直连" : "代理")}隧道。");
                    phase = "隧道传输";
                    using var relay = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    var remote = upstream.GetStream();
                    var a = PumpAsync(stream, remote, upstream.Client, relay.Token);
                    var b = PumpAsync(remote, stream, client.Client, relay.Token);
                    var first = await Task.WhenAny(a, b);
                    if (first.IsFaulted || first.IsCanceled) await relay.CancelAsync();
                    try { await Task.WhenAll(a, b); }
                    catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
                    { Message?.Invoke($"{destination}: 隧道传输结束，{DescribeFailure(e)}。"); }
                }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ArgumentException or AuthenticationException)
            {
                if (destination is not null) Message?.Invoke($"{destination}: {phase}未完成，{DescribeFailure(e)}。");
                // Once the tunnel is established we must never inject a SOCKS error into application bytes.
                if (!established)
                    try { await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 0, 0, 0, 0, 0, 0 }, _stop.Token); } catch { }
            }
        }
    }

    private static async Task PumpAsync(Stream source, Stream destination, Socket target, CancellationToken ct)
    {
        var buffer = new byte[32768];
        while (true)
        {
            // Established WebSocket/SSE tunnels may legitimately be idle for a long time.
            // Bound setup/probes and connection count, not the lifetime of application streams.
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0)
            {
                try { target.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
                return;
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private async Task<ProbeResult> ProbeAsync(string host, int port, ConnectionRoute route, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(route == ConnectionRoute.Direct ? _options.DirectTimeoutMs : _options.ProxyTimeoutMs);
        var phase = route == ConnectionRoute.Direct ? "直连建连" : "代理通道建连";
        try
        {
            using var client = await OpenAsync(host, port, route, bounded.Token);
            if (port == 443)
            {
                phase = "TLS 握手与证书校验";
                using var tls = new SslStream(client.GetStream(), false);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host, ApplicationProtocols = [SslApplicationProtocol.Http11]
                }, bounded.Token);
                return new(true, "TLS certificate and handshake verified");
            }
            phase = "HTTP 响应检测";
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HEAD / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\nUser-Agent: v2rayN-limited-learning/1\r\n\r\n"), bounded.Token);
            var prefix = new byte[12]; await stream.ReadExactlyAsync(prefix, bounded.Token);
            var line = Encoding.ASCII.GetString(prefix);
            var valid = (line.StartsWith("HTTP/1.1 ") || line.StartsWith("HTTP/1.0 ")) &&
                int.TryParse(line.AsSpan(9, 3), out var status) && status is >= 100 and <= 599;
            return new(valid, valid ? "HTTP response received (status is not a block classifier)" : "Invalid HTTP response");
        }
        catch (Exception e) when (e is IOException or SocketException or AuthenticationException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return new(false, $"{phase}: {DescribeFailure(e)}");
        }
    }

    private static string DescribeFailure(Exception error) => error switch
    {
        OperationCanceledException => "已取消或达到超时限制",
        SocketException socket => $"网络错误 {socket.SocketErrorCode}",
        AuthenticationException => "TLS 认证失败（证书或握手不兼容）",
        IOException { InnerException: SocketException socket } => $"网络错误 {socket.SocketErrorCode}",
        IOException io when io.Message.StartsWith("Upstream SOCKS", StringComparison.Ordinal) => io.Message,
        IOException => "连接关闭或读写失败",
        _ => error.GetType().Name
    };

    private async Task<TcpClient> OpenAsync(string host, int port, ConnectionRoute route, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        try
        {
            if (route == ConnectionRoute.Direct)
                await client.ConnectAsync(host, port, ct);
            else
            {
                await client.ConnectAsync(IPAddress.Loopback, _options.ProxyPort, ct);
                var stream = client.GetStream();
                await stream.WriteAsync(new byte[] { 5, 1, 0 }, ct);
                var reply = new byte[2]; await stream.ReadExactlyAsync(reply, ct);
                if (reply[0] != 5 || reply[1] != 0) throw new IOException("Upstream SOCKS authentication failed");
                var name = Encoding.ASCII.GetBytes(host);
                var request = new byte[name.Length + 7];
                request[0] = 5; request[1] = 1; request[3] = 3; request[4] = checked((byte)name.Length);
                name.CopyTo(request, 5); request[^2] = (byte)(port >> 8); request[^1] = (byte)(port & 255);
                await stream.WriteAsync(request, ct);
                var head = new byte[4]; await stream.ReadExactlyAsync(head, ct);
                if (head[0] != 5 || head[1] != 0) throw new IOException("Upstream SOCKS connection failed");
                await ReadHostAsync(stream, head[3], ct); await stream.ReadExactlyAsync(reply, ct);
            }
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static async Task<string> ReadHostAsync(Stream stream, byte kind, CancellationToken ct)
    {
        var count = kind switch { 1 => 4, 4 => 16, 3 => 0, _ => throw new IOException("Invalid SOCKS address") };
        if (kind == 3) { var len = new byte[1]; await stream.ReadExactlyAsync(len, ct); count = len[0]; }
        var bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, ct);
        return kind == 3 ? Encoding.ASCII.GetString(bytes) : new IPAddress(bytes).ToString();
    }

    private async Task<IPAddress[]> ResolveLocalAsync(string host, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(_options.DirectTimeoutMs);
        try { return await Dns.GetHostAddressesAsync(host, bounded.Token); }
        catch (SocketException) { return []; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return []; }
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        var b = ip.GetAddressBytes();
        if (b.Length == 16) return ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || (b[0] & 0xfe) == 0xfc;
        return b[0] is 0 or 10 or 127 || (b[0] == 172 && b[1] is >= 16 and <= 31) ||
            (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) ||
            (b[0] == 100 && b[1] is >= 64 and <= 127) || b[0] >= 224;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); _listener.Stop();
        if (_accept is not null) await _accept;
        try { await Task.WhenAll(_tasks.Values).WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        _stop.Dispose();
    }
}
