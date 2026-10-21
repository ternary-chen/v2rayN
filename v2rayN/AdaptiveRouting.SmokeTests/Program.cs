using AdaptiveRouting;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Diagnostics;

var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
void Assert(bool value, string why) { if (!value) throw new Exception(why); }
async Task Test(string name, Func<Task> test) { await test(); Console.WriteLine("PASS " + name); passed++; }
LearningStore Store(string name) => new(Path.Combine(root, name + ".json"));
LearningOptions Options() => new() { Enabled = true, ListenerPort = FreePort(), ProxyPort = FreePort() };
string Config(params JsonObject[] rules) => new JsonObject
{
    ["inbounds"] = new JsonArray(),
    ["outbounds"] = new JsonArray(new JsonObject { ["tag"] = "proxy" }, new JsonObject { ["tag"] = "direct" }),
    ["routing"] = new JsonObject { ["rules"] = new JsonArray(rules.Select(x => (JsonNode)x).ToArray()) }
}.ToJsonString();
JsonObject Rule(string target, string? domain = null) => domain is null
    ? new() { ["type"] = "field", ["network"] = "tcp,udp", ["outboundTag"] = target }
    : new() { ["type"] = "field", ["domain"] = new JsonArray(domain), ["outboundTag"] = target };

await Test("Community decisions remain ahead of unknown-only learning", () =>
{
    var r = JsonNode.Parse(XrayLearningConfig.Apply(Config(Rule("direct", "domain:local.example"),
        Rule("proxy", "domain:openai.com"), Rule("direct")), Options()))!["routing"]!["rules"]!.AsArray();
    var directIndex = r.Select((x, i) => (x, i)).First(x => x.x?["domain"]?[0]?.ToString() == "domain:local.example").i;
    var proxyIndex = r.Select((x, i) => (x, i)).First(x => x.x?["domain"]?[0]?.ToString() == "domain:openai.com").i;
    var learnIndex = r.Select((x, i) => (x, i)).First(x => x.x?["outboundTag"]?.ToString() == XrayLearningConfig.UnknownTag).i;
    Assert(directIndex < proxyIndex && proxyIndex < learnIndex, "Rule order changed");
    Assert(r[1]?["process"]?.AsArray().Any(x => x?.ToString() == "NTEGame") == true, "Process exceptions missing");
    Assert(r[^1]?["outboundTag"]?.ToString() == "direct", "Default must remain direct");
    Assert(r[0]?["inboundTag"]?[0]?.ToString() == XrayLearningConfig.ForcedProxyTag, "Proxy probe may loop into learning");
    return Task.CompletedTask;
});
await Test("Disabled learning retains process exclusions but adds no learning listener", () =>
{
    var options = Options(); options.Enabled = false;
    var r = JsonNode.Parse(XrayLearningConfig.Apply(Config(Rule("direct")), options))!;
    Assert(r["inbounds"]!.AsArray().Count == 0, "Disabled feature added listener");
    Assert(r["routing"]!["rules"]![0]?["process"] is not null, "Direct programs must remain protected");
    return Task.CompletedTask;
});
await Test("Global proxy rule cannot be silently weakened", () =>
{
    try { XrayLearningConfig.Apply(Config(Rule("proxy")), Options()); throw new Exception("Should reject global proxy"); }
    catch (InvalidOperationException) { }
    return Task.CompletedTask;
});
await Test("Direct success never probes proxy or learns", async () =>
{
    var store = Store("direct"); var calls = new List<ConnectionRoute>();
    var d = new LearningDecision(store, Options(), "node-a", (_, _, r, _) =>
    { calls.Add(r); return Task.FromResult(new ProbeResult(true, "ok")); });
    Assert(await d.DecideAsync("example.com", 443, default) == ConnectionRoute.Direct, "Expected direct");
    Assert(await d.DecideAsync("example.com", 443, default) == ConnectionRoute.Direct, "Expected cached direct");
    Assert(calls.SequenceEqual(new[] { ConnectionRoute.Direct }) && store.Snapshot().Count == 0, "Unexpected learning");
});
await Test("Direct failure plus verified proxy success learns exact host only", async () =>
{
    var store = Store("proxy");
    var d = new LearningDecision(store, Options(), "node-a", (_, _, r, _) => Task.FromResult(new ProbeResult(r == ConnectionRoute.Proxy, "test")));
    Assert(await d.DecideAsync("api.example.com", 443, default) == ConnectionRoute.Proxy, "Expected proxy");
    Assert(store.Contains("api.example.com", 443, "node-a"), "Record missing");
    Assert(!store.Contains("example.com", 443, "node-a") && !store.Contains("api.example.com", 443, "node-b"), "Record scope widened");
    Assert(new LearningStore(Path.Combine(root,"proxy.json")).Snapshot().Count == 1, "Record did not persist");
});
await Test("Both failures never learn and repeated failures have a cooldown", async () =>
{
    var store = Store("fail"); var calls = 0;
    var d = new LearningDecision(store, Options(), "a", (_, _, _, _) => { calls++; return Task.FromResult(new ProbeResult(false, "fail")); });
    for (var i = 0; i < 2; i++)
        try { await d.DecideAsync("bad.example", 443, default); throw new Exception("Should fail"); } catch (IOException) { }
    Assert(store.Snapshot().Count == 0 && calls == 2, "Failed route learned or probes repeated");
});
await Test("Concurrent requests to one host share a learning decision", async () =>
{
    var calls = 0; var d = new LearningDecision(Store("concurrent"), Options(), "a", async (_, _, r, ct) =>
    { Interlocked.Increment(ref calls); await Task.Delay(5, ct); return new(r == ConnectionRoute.Proxy, "test"); });
    var outcomes = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => d.DecideAsync("concurrent.example", 443, default)));
    Assert(calls == 2 && outcomes.All(x => x == ConnectionRoute.Proxy), "Duplicate probes");
});
await Test("Cancellation never becomes a learned proxy", async () =>
{
    var store = Store("cancel"); using var stop = new CancellationTokenSource();
    var d = new LearningDecision(store, Options(), "a", (_, _, _, ct) => { stop.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(new ProbeResult(false, "")); });
    try { await d.DecideAsync("cancel.example", 443, stop.Token); throw new Exception("Should cancel"); } catch (OperationCanceledException) { }
    Assert(store.Snapshot().Count == 0, "Cancellation learned");
});
await Test("Expiration, capacity and explicit forget", () =>
{
    var now = DateTimeOffset.UtcNow; var store = new LearningStore(Path.Combine(root,"expiry.json"), 2, () => now);
    foreach (var h in new[] { "a.example", "b.example", "c.example" }) store.Learn(h,443,"a",TimeSpan.FromHours(1),"test");
    Assert(store.Snapshot().Count == 2 && !store.Contains("a.example",443,"a"), "Capacity not bounded");
    store.Forget("b.example",443,"a"); Assert(store.Snapshot().Count == 1,"Forget failed");
    now = now.AddHours(2); Assert(store.Snapshot().Count == 0,"Expired record still active");
    return Task.CompletedTask;
});
await Test("Forgetting an old-node record does not remove the current-node record", () =>
{
    var store=Store("forget-node");
    foreach(var id in new[]{"old","current"})store.Learn("same.example",443,id,TimeSpan.FromHours(1),"test");
    var decision=new LearningDecision(store,Options(),"current",(_,_,_,_)=>Task.FromResult(new ProbeResult(true,"test")));
    decision.Forget("same.example",443,"old");
    Assert(!store.Contains("same.example",443,"old")&&store.Contains("same.example",443,"current"),"Wrong node removed");
    return Task.CompletedTask;
});
await Test("Local and non-web destinations do not learn", () =>
{
    foreach(var h in new[]{"localhost","printer.lan","router.local","10.0.0.1","::1"})
        Assert(!LearningStore.IsLearningCandidate(h,443),h);
    Assert(!LearningStore.IsLearningCandidate("example.com",22),"SSH learning");
    Assert(LearningStore.NormalizeHost("WWW.Example.COM.")=="www.example.com","Canonicalization");
    Assert(LearningProxy.IsPrivate(IPAddress.Parse("::ffff:192.168.1.1")),"Mapped local IP");
    return Task.CompletedTask;
});
await Test("Codex edit preserves unrelated settings, newline style and comments", () =>
{
    var source="model = \"test\"\r\n[features]\r\nrespect_system_proxy = false # keep\r\nother = true\r\n[other]\r\nvalue = 1\r\n";
    var edited=CodexProxySupport.EnableSystemProxy(source);
    Assert(edited==source.Replace("false # keep","true # keep"),"Unrelated settings changed");
    Assert(CodexProxySupport.EnableSystemProxy(edited)==edited,"Not idempotent");
    Assert(CodexProxySupport.EnableSystemProxy("[features]\nother=true\n").Contains("respect_system_proxy = true"),"Insert failed");
    return Task.CompletedTask;
});
await Test("Ambiguous Codex TOML is refused", () =>
{
    foreach(var text in new[]{"features = { respect_system_proxy = false }", "[features]\n[features]\n", "[features]\nrespect_system_proxy = 1", "x = \"\"\"text\"\"\""})
    {
        try { CodexProxySupport.EnableSystemProxy(text); throw new Exception("Should reject: "+text); } catch(InvalidOperationException) { }
    }
    return Task.CompletedTask;
});
await Test("Codex backups restore exactly and refuse overwriting later edits", () =>
{
    var home=Path.Combine(root,"codex");Directory.CreateDirectory(home);var file=Path.Combine(home,"config.toml");
    File.WriteAllText(file,"model=\"test\"\n");var before=File.ReadAllBytes(file);var change=CodexProxySupport.Apply(home)!;
    Assert(CodexProxySupport.Apply(home) is null,"Repeated apply must preserve original undo");
    CodexProxySupport.Undo(home,change);Assert(File.ReadAllBytes(file).SequenceEqual(before),"Backup mismatch");
    change=CodexProxySupport.Apply(home)!;File.AppendAllText(file,"# newer user change\n");
    try {CodexProxySupport.Undo(home,change);throw new Exception("Should reject stale undo");}catch(IOException) { }
    return Task.CompletedTask;
});
await Test("SOCKS rejects unsupported UDP without learning", async () =>
{
    // Loopback destination is always direct, regardless of probe state.
    var options=Options();await using var proxy=new LearningProxy(options,Path.Combine(root,"wire.json"),"test");proxy.Start();
    using var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,options.ListenerPort);
    var stream=client.GetStream();using var deadline=new CancellationTokenSource(3000);
    await stream.WriteAsync(new byte[]{5,1,0},deadline.Token);var reply=new byte[2];await stream.ReadExactlyAsync(reply,deadline.Token);
    Assert(reply.SequenceEqual(new byte[]{5,0}),"SOCKS handshake");
    // Unsupported commands must fail before tunnel establishment, never become proxy records.
    await stream.WriteAsync(new byte[]{5,3,0,1,127,0,0,1,0,80},deadline.Token);
    var error=new byte[10];await stream.ReadExactlyAsync(error,deadline.Token);
    Assert(error[1]!=0 && proxy.Store.Snapshot().Count==0,"Unsupported UDP accepted");
});
await Test("Real SOCKS fallback relays a POST exactly once after independent HEAD probe", async () =>
{
    var options=Options(); var heads=0; var posts=0;
    var upstream=new TcpListener(IPAddress.Loopback,options.ProxyPort);upstream.Start();
    using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var server=Task.Run(async () =>
    {
        for(var i=0;i<2;i++)
        {
            using var socket=await upstream.AcceptTcpClientAsync(stop.Token);var stream=socket.GetStream();
            var hello=new byte[3];await stream.ReadExactlyAsync(hello,stop.Token);
            await stream.WriteAsync(new byte[]{5,0},stop.Token);
            var head=new byte[5];await stream.ReadExactlyAsync(head,stop.Token);
            var address=new byte[head[4]+2];await stream.ReadExactlyAsync(address,stop.Token);
            await stream.WriteAsync(new byte[]{5,0,0,1,0,0,0,0,0,0},stop.Token);
            var content=new List<byte>();var one=new byte[1];
            while(!Encoding.ASCII.GetString(content.ToArray()).EndsWith("\r\n\r\n"))
            {await stream.ReadExactlyAsync(one,stop.Token);content.Add(one[0]);}
            var request=Encoding.ASCII.GetString(content.ToArray());
            if(request.StartsWith("HEAD / ")) heads++;
            else
            {
                Assert(request.StartsWith("POST /submit "),"Original request changed");
                var body=new byte[4];await stream.ReadExactlyAsync(body,stop.Token);
                Assert(Encoding.ASCII.GetString(body)=="test","Request body corrupted");posts++;
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"),stop.Token);
        }
    });
    try
    {
        await using var proxy=new LearningProxy(options,Path.Combine(root,"relay.json"),"test");proxy.Start();
        using var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,options.ListenerPort,stop.Token);
        var stream=client.GetStream();await stream.WriteAsync(new byte[]{5,1,0},stop.Token);
        var hello=new byte[2];await stream.ReadExactlyAsync(hello,stop.Token);
        var host=Encoding.ASCII.GetBytes("learning-test.invalid");
        await stream.WriteAsync(new byte[]{5,1,0,3,(byte)host.Length}.Concat(host).Concat(new byte[]{0,80}).ToArray(),stop.Token);
        var reply=new byte[10];await stream.ReadExactlyAsync(reply,stop.Token);Assert(reply[1]==0,"Proxy fallback failed");
        await stream.WriteAsync(Encoding.ASCII.GetBytes("POST /submit HTTP/1.1\r\nHost: learning-test.invalid\r\nContent-Length: 4\r\n\r\ntest"),stop.Token);
        using var memory=new MemoryStream();await stream.CopyToAsync(memory,stop.Token);
        Assert(Encoding.ASCII.GetString(memory.ToArray()).EndsWith("OK"),"Response not relayed");
        await server;Assert(heads==1&&posts==1,"Application request was duplicated");
        Assert(proxy.Store.Snapshot().Single().Host=="learning-test.invalid","Learned host missing");
    }
    finally {upstream.Stop();stop.Cancel();}
});
await Test("WebSocket Upgrade and masked frames pass through unchanged", async () =>
{
    var options=Options();var upstream=new TcpListener(IPAddress.Loopback,options.ProxyPort);upstream.Start();
    using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));
    byte[] clientFrame=[0x81,0x82,1,2,3,4,(byte)('o'^1),(byte)('k'^2)];
    var server=Task.Run(async()=>
    {
        for(var i=0;i<2;i++)
        {
            using var socket=await upstream.AcceptTcpClientAsync(stop.Token);var stream=socket.GetStream();
            var hello=new byte[3];await stream.ReadExactlyAsync(hello,stop.Token);await stream.WriteAsync(new byte[]{5,0},stop.Token);
            var head=new byte[5];await stream.ReadExactlyAsync(head,stop.Token);var addr=new byte[head[4]+2];await stream.ReadExactlyAsync(addr,stop.Token);
            await stream.WriteAsync(new byte[]{5,0,0,1,0,0,0,0,0,0},stop.Token);
            var request=await ReadHeaders(stream,stop.Token);
            if(i==0)
            {
                Assert(request.StartsWith("HEAD / "),"Missing independent probe");
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"),stop.Token);
            }
            else
            {
                Assert(request.Contains("Upgrade: websocket\r\n"),"Upgrade header modified");
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n"),stop.Token);
                var actual=new byte[clientFrame.Length];await stream.ReadExactlyAsync(actual,stop.Token);
                Assert(actual.SequenceEqual(clientFrame),"WebSocket frame modified");
                await stream.WriteAsync(new byte[]{0x81,2,(byte)'o',(byte)'k'},stop.Token);
            }
        }
    });
    try
    {
        await using var proxy=new LearningProxy(options,Path.Combine(root,"websocket.json"),"test");proxy.Start();
        using var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,options.ListenerPort,stop.Token);var stream=client.GetStream();
        await stream.WriteAsync(new byte[]{5,1,0},stop.Token);var hello=new byte[2];await stream.ReadExactlyAsync(hello,stop.Token);
        var host=Encoding.ASCII.GetBytes("websocket-test.invalid");
        await stream.WriteAsync(new byte[]{5,1,0,3,(byte)host.Length}.Concat(host).Concat(new byte[]{0,80}).ToArray(),stop.Token);
        var reply=new byte[10];await stream.ReadExactlyAsync(reply,stop.Token);Assert(reply[1]==0,"SOCKS fallback failed");
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /socket HTTP/1.1\r\nHost: websocket-test.invalid\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n"),stop.Token);
        Assert((await ReadHeaders(stream,stop.Token)).StartsWith("HTTP/1.1 101"),"Upgrade response changed");
        await stream.WriteAsync(clientFrame,stop.Token);var response=new byte[4];await stream.ReadExactlyAsync(response,stop.Token);
        Assert(response.SequenceEqual(new byte[]{0x81,2,(byte)'o',(byte)'k'}),"Return frame changed");
        await server;
    }
    finally{upstream.Stop();stop.Cancel();}
});
if(Environment.GetEnvironmentVariable("TEST_XRAY_EXE") is {Length:>0} xray)
    await Test("Installed Xray accepts the generated learning configuration", async () =>
    {
        var options=Options();var config=JsonNode.Parse(Config(Rule("direct","domain:internal.example"),Rule("proxy","domain:openai.com"),Rule("direct")))!;
        config["outbounds"]![0]!["protocol"]="freedom";config["outbounds"]![1]!["protocol"]="freedom";
        var path=Path.Combine(root,"xray-validation.json");File.WriteAllText(path,XrayLearningConfig.Apply(config.ToJsonString(),options));
        var start=new ProcessStartInfo(xray){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"run","-test","-config",path})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)!;
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert(process.ExitCode==0,(await stdout)+(await stderr));
    });
Console.WriteLine($"{passed} targeted checks passed.");

static int FreePort()
{
    var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;
}

static async Task<string> ReadHeaders(Stream stream,CancellationToken ct)
{
    var bytes=new List<byte>();var one=new byte[1];
    while(bytes.Count<8192)
    {
        await stream.ReadExactlyAsync(one,ct);bytes.Add(one[0]);
        var text=Encoding.ASCII.GetString(bytes.ToArray());if(text.EndsWith("\r\n\r\n"))return text;
    }
    throw new IOException("Header too long");
}
