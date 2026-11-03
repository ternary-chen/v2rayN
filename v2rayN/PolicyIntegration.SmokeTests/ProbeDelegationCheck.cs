using AdaptiveRouting;
using System.Net;
using System.Net.Sockets;
using System.Text;

internal static class ProbeDelegationCheck
{
    public static async Task Run()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct = timeout.Token;
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var options = new LearningOptions { Enabled = true, ListenerPort = Port(), ProxyPort = Port(), TunDirectPort = ((IPEndPoint)upstream.LocalEndpoint).Port };
        var requests = new List<string>();
        var server = Task.Run(async () =>
        {
            for (var n = 0; n < 2; n++)
            {
                using var peer = await upstream.AcceptTcpClientAsync(ct);
                var s = peer.GetStream();
                var hello = new byte[3]; await s.ReadExactlyAsync(hello, ct);
                if (!hello.SequenceEqual(new byte[] { 5, 1, 0 })) throw new Exception("Missing delegated SOCKS greeting");
                await s.WriteAsync(new byte[] { 5, 0 }, ct);
                var header = new byte[5]; await s.ReadExactlyAsync(header, ct);
                if (header[3] != 3) throw new Exception("Learner resolved the hostname locally");
                var name = new byte[header[4]]; await s.ReadExactlyAsync(name, ct);
                var port = new byte[2]; await s.ReadExactlyAsync(port, ct);
                if (Encoding.ASCII.GetString(name) != "probe-delegation.example") throw new Exception("Lost target hostname");
                await s.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, ct);
                var request = new StringBuilder(); var one = new byte[1];
                while (!request.ToString().EndsWith("\r\n\r\n"))
                {
                    await s.ReadExactlyAsync(one, ct); request.Append((char)one[0]);
                    if (request.Length > 4096) throw new Exception("Unexpected request size");
                }
                requests.Add(request.ToString());
                await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), ct);
            }
        }, ct);
        var data = Path.Combine(AppContext.BaseDirectory, "probe-check-" + Guid.NewGuid().ToString("N") + ".json");
        await using var learner = new LearningProxy(options, data, "isolated-probe", coreManagedDialing: true);
        learner.Start();
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, options.ListenerPort, ct);
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, ct); var ack = new byte[2]; await stream.ReadExactlyAsync(ack, ct);
        var target = Encoding.ASCII.GetBytes("probe-delegation.example");
        await stream.WriteAsync(new byte[] { 5, 1, 0, 3, (byte)target.Length }.Concat(target).Concat(new byte[] { 0, 80 }).ToArray(), ct);
        var connected = new byte[10]; await stream.ReadExactlyAsync(connected, ct);
        if (connected[1] != 0) throw new Exception("Core-managed direct probe failed");
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: probe-delegation.example\r\nCookie: fixture=yes\r\nAuthorization: Bearer synthetic-test-only\r\n\r\n"), ct);
        var result = new byte[12]; await stream.ReadExactlyAsync(result, ct);
        await server;
        if (requests.Count != 2 || !requests[0].StartsWith("HEAD /") || requests[0].Contains("Cookie:") || requests[0].Contains("Authorization:"))
            throw new Exception("Probe copied application credentials");
        if (!requests[1].StartsWith("GET /") || !requests[1].Contains("Cookie: fixture=yes")) throw new Exception("Application stream was modified");
        Console.WriteLine("PASS Core-managed direct probe keeps DNS/host in SOCKS, sends no credentials, then relays the real request separately");
    }

    private static int Port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
