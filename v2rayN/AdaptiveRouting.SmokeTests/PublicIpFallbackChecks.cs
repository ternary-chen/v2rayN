using AdaptiveRouting;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

internal static class PublicIpFallbackChecks
{
    public static async Task RunAsync()
    {
        await Check("IPv4 timeout falls back once, preserves bytes and caches proxy", "203.0.113.9", "stall", "echo", true, 2);
        await Check("IPv6 rejection falls back without TLS probing", "2001:db8::9", "reject", "echo", true);
        await Check("Successful direct connection never retries after application EOF", "203.0.113.9", "echo", "echo", true);
        await Check("Private IPv4 never falls back", "192.168.1.9", "reject", "echo", false);
        await Check("Mapped private IPv6 never falls back", "::ffff:192.168.1.9", "reject", "echo", false);
        await Check("Private IPv6 never falls back", "fd00::9", "reject", "echo", false);
        await Check("Both routes failing returns SOCKS failure without learning", "203.0.113.9", "reject", "reject", false);
        await Check("Proxy timeout is bounded", "203.0.113.9", "reject", "stall", false);
        await Check("Runtime cancellation never starts fallback", "203.0.113.9", "stall", "echo", false, cancel: true);
        Console.WriteLine("9 public-IP targeted checks passed (loopback mock exits only).");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static async Task Check(string name, string host, string directMode, string proxyMode,
        bool success, int requests = 1, bool cancel = false)
    {
        // Documentation-only target addresses are carried inside SOCKS requests. All actual
        // sockets connect to loopback; neither Telegram nor any public network is contacted.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var direct = new MockExit(directMode, deadline.Token);
        await using var remote = new MockExit(proxyMode, deadline.Token);
        var reserve = new TcpListener(IPAddress.Loopback, 0);
        reserve.Start();
        var port = ((IPEndPoint)reserve.LocalEndpoint).Port;
        reserve.Stop();
        var options = new LearningOptions
        {
            ListenerPort = port, TunDirectPort = direct.Port, ProxyPort = remote.Port,
            DirectTimeoutMs = cancel ? 5000 : 500, ProxyTimeoutMs = 1000
        };
        var path = Path.Combine(Path.GetTempPath(), "v2rayn-ip-check-" + Guid.NewGuid().ToString("N") + ".json");
        var proxy = new LearningProxy(options, path, "isolated-test", coreManagedDialing: true);
        var disposed = false;
        var events = new ConcurrentQueue<LearningConnectionEvent>();
        proxy.ConnectionEvent += events.Enqueue;
        proxy.Start();
        try
        {
            for (var i = 0; i < requests; i++)
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
                var stream = client.GetStream();
                await stream.WriteAsync(new byte[] { 5, 1, 0 }, deadline.Token);
                var hello = new byte[2];
                await stream.ReadExactlyAsync(hello, deadline.Token);
                Assert(hello.SequenceEqual(new byte[] { 5, 0 }), "Bad SOCKS greeting");
                var address = IPAddress.Parse(host).GetAddressBytes();
                var request = new byte[] { 5, 1, 0, (byte)(address.Length == 4 ? 1 : 4) }
                    .Concat(address).Concat(new byte[] { 1, 187 }).ToArray();
                byte[] payload = [0xef, 0x04, 0x00, 0x81, 0x42, 0xff];
                // Pipeline bytes before CONNECT succeeds: they must remain unread while switching.
                await stream.WriteAsync(request.Concat(payload).ToArray(), deadline.Token);
                if (cancel)
                {
                    await direct.Accepted.Task.WaitAsync(deadline.Token);
                    await proxy.DisposeAsync();
                    disposed = true;
                    Assert(remote.Connections == 0, "Cancellation started a proxy connection");
                    break;
                }
                var reply = new byte[10];
                await stream.ReadExactlyAsync(reply, deadline.Token);
                Assert((reply[1] == 0) == success, "Unexpected SOCKS result");
                if (success)
                {
                    var response = new byte[payload.Length];
                    await stream.ReadExactlyAsync(response, deadline.Token);
                    Assert(response.SequenceEqual(payload), "Probe bytes were inserted or payload changed");
                    Assert(await stream.ReadAsync(new byte[1], deadline.Token) == 0, "Expected upstream EOF");
                }
            }
            Assert(proxy.Store.Snapshot(true).Count == 0 && !File.Exists(path), "IP was persisted as learned domain");
            if (!cancel)
            {
                var needsFallback = !LearningProxy.IsPrivate(IPAddress.Parse(host)) && directMode != "echo";
                Assert(direct.Connections == (requests == 2 ? 1 : requests), "Direct cache did not avoid repeat timeout");
                Assert(remote.Connections == (needsFallback ? requests : 0), "Wrong fallback count or request replay");
                if (success)
                    Assert(events.Any(x => x.Stage == "隧道已建立" && x.Route ==
                        (needsFallback ? ConnectionRoute.Proxy : ConnectionRoute.Direct)), "Final route not logged");
                else Assert(!events.Any(x => x.Stage == "隧道已建立"), "Failed route reported established");
            }
            direct.ThrowIfFailed();
            remote.ThrowIfFailed();
            Console.WriteLine("PASS " + name);
        }
        finally { if (!disposed) await proxy.DisposeAsync(); }
    }

    private sealed class MockExit : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop;
        private readonly Task _loop;
        private readonly ConcurrentBag<Task> _clients = new();
        private int _connections;
        public int Connections => Volatile.Read(ref _connections);
        public int Port { get; }
        public TaskCompletionSource Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MockExit(string mode, CancellationToken token)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        Interlocked.Increment(ref _connections);
                        Accepted.TrySetResult();
                        _clients.Add(Handle(client, mode));
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            });
        }

        private async Task Handle(TcpClient client, string mode)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    if (mode == "stall") { await Task.Delay(Timeout.Infinite, _stop.Token); return; }
                    var hello = new byte[3];
                    await stream.ReadExactlyAsync(hello, _stop.Token);
                    await stream.WriteAsync(new byte[] { 5, 0 }, _stop.Token);
                    var header = new byte[5];
                    await stream.ReadExactlyAsync(header, _stop.Token);
                    Assert(header[3] == 3, "Expected domain-form upstream request");
                    var target = new byte[header[4] + 2];
                    await stream.ReadExactlyAsync(target, _stop.Token);
                    Assert(IPAddress.TryParse(Encoding.ASCII.GetString(target, 0, target.Length - 2), out _), "Target IP changed");
                    await stream.WriteAsync(new byte[] { 5, (byte)(mode == "reject" ? 1 : 0), 0, 1, 0, 0, 0, 0, 0, 0 }, _stop.Token);
                    if (mode == "reject") return;
                    var payload = new byte[6];
                    await stream.ReadExactlyAsync(payload, _stop.Token);
                    Assert(payload.SequenceEqual(new byte[] { 0xef, 0x04, 0, 0x81, 0x42, 0xff }), "Unexpected HTTP/TLS probe or replay");
                    await stream.WriteAsync(payload, _stop.Token);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            }
        }

        public void ThrowIfFailed()
        {
            foreach (var task in _clients) if (task.IsFaulted) throw task.Exception!;
            if (_loop.IsFaulted) throw _loop.Exception!;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _loop;
            _listener.Stop();
            await Task.WhenAll(_clients);
            _stop.Dispose();
        }
    }
}
