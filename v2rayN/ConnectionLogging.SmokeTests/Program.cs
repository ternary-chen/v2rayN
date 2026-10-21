using AdaptiveRouting;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
int count = 0;
async Task Test(string name, Func<Task> run) { await run(); count++; Console.WriteLine("PASS " + name); }
var ports = new Dictionary<string,int> { ["socks"] = 18808, ["limited-learning-proxy"] = 17881 };
var formatter = new ConnectionLogFormatter("日常香港", "美国专线", ports);
string Raw(string tag = "proxy", string source = "127.0.0.1:3279", string destination = "//mtalk.google.com:5228", string inbound = "socks", string arrow = "->") =>
    $"2026/10/21 19:45:36.529596 from {source} accepted {destination} [{inbound} {arrow} {tag}]";
XrayAccessEntry Entry(string tag = "proxy") => ConnectionLogFormatter.Parse(Raw(tag))!;
var owner = new TcpProcessOwner(new(IPAddress.Loopback,3279),new(IPAddress.Loopback,18808),42,"browser.exe");

await Test("Xray access formats retain original text and distinguish AI/default/direct/blocked", () =>
{
    foreach (var arrow in new[] { "->", ">>", "==>" })
        Check(ConnectionLogFormatter.Parse(Raw(arrow:arrow))?.Outbound=="proxy", "Arrow parse failed");
    var line=formatter.Format(Entry(),[owner]);
    Check(line.StartsWith(Raw()) && line.Contains("browser.exe (PID 42)") && line.Contains("策略:默认策略") && line.Contains("节点:日常香港"),"Default labels wrong");
    Check(formatter.Format(Entry("ai-policy"),[owner]).Contains("节点:美国专线"),"AI node wrong");
    Check(formatter.Format(Entry("direct"),[owner]).Contains("节点:无（直连）"),"Direct falsely attributed to a node");
    Check(formatter.Format(Entry("block"),[owner]).Contains("策略:阻止"),"Block wrong");
    Check(formatter.Format(Entry("new-tag"),[owner]).Contains("节点:未知"),"Unknown exit guessed");
    Check(ConnectionLogFormatter.Parse("[Warning] dial failed") is null,"Errors changed");
    return Task.CompletedTask;
});
await Test("PID lookup requires full client endpoint and proxy port, rejects stale/ambiguous/LAN/UDP matches", () =>
{
    Check(TcpProcessSnapshot.Find([owner with { Remote=new(IPAddress.Loopback,17881) }],owner.Local,18808) is null,"Wrong inlet port matched");
    Check(TcpProcessSnapshot.Find([owner,owner with { Pid=43 }],owner.Local,18808) is null,"Ambiguous process guessed");
    Check(formatter.Format(ConnectionLogFormatter.Parse(Raw(source:"192.168.1.10:3279"))!,[owner]).Contains("非本机客户端"),"LAN spoofed local process");
    Check(formatter.Format(ConnectionLogFormatter.Parse(Raw(destination:"udp:example.com:443"))!,[owner]).Contains("UDP 未提供"),"UDP reused TCP PID");
    Check(formatter.Format(Entry(),[]).Contains("进程:未知"),"Ended connection guessed");
    Check(formatter.Format(Entry(),[],"超时").Contains("未知（超时）"),"Failure reason missing");
    return Task.CompletedTask;
});
await Test("macOS machine-readable lsof parser handles spaces, IPv6 and multiple sockets", () =>
{
    var rows=TcpProcessSnapshot.ParseLsof("p42\ncGoogle Chrome Helper\nf7\nn127.0.0.1:3279->127.0.0.1:18808\np43\ncBrowser\nf9\nn[::1]:3280->[::1]:18808\nn*:18808\np44\ncOther\nn127.0.0.1:3279->127.0.0.1:20000\n");
    Check(rows.Count==3,"Bad lsof row count");
    Check(TcpProcessSnapshot.Find(rows,owner.Local,18808)?.Name=="Google Chrome Helper","Spaces or exact socket lost");
    var line=formatter.Format(ConnectionLogFormatter.Parse(Raw(source:"[::1]:3280",destination:"tcp:api.openai.com:443",tag:"ai-policy"))!,rows);
    Check(line.Contains("Browser (PID 43)") && line.Contains("AI 策略"),"IPv6 mapping wrong");
    return Task.CompletedTask;
});
await Test("Running config snapshots survive UI selection changes; labels cannot inject log lines", () =>
{
    var mutablePorts=new Dictionary<string,int>(ports);
    var snapshot=new ConnectionLogFormatter("old-node\n[fake]", "AI", mutablePorts);
    mutablePorts["socks"]=9999;
    var line=snapshot.Format(Entry(),[owner]);
    Check(line.Contains("PID 42") && !line.Contains('\n') && line.Contains("节点:old-nodefake"),"Mutable ports or label injection");
    var extracted=ConnectionLogFormatter.ReadInboundPorts("{\"inbounds\":[{\"tag\":\"socks\",\"port\":18808,\"settings\":{\"password\":\"secret\"}}]}");
    Check(extracted.Count==1 && extracted["socks"]==18808,"Port extraction wrong");
    return Task.CompletedTask;
});
await Test("Learning logs distinguish pending, chosen and connected routes without inventing original process", () =>
{
    Check(formatter.Format(Entry("limited-learning"),[owner]).Contains("待检测，尚未选择出口"),"Pending routed as default");
    var line=formatter.FormatLearning(new(7,"example.com:443",ConnectionRoute.Direct,"隧道已建立"));
    Check(line.Contains("学习连接#7") && line.Contains("节点:无（直连）") && line.Contains("未携带原进程"),"Learning origin or direct wrong");
    Check(formatter.FormatLearning(new(8,"example.com:443",ConnectionRoute.Proxy,"隧道已建立")).Contains("节点:日常香港"),"Learning used AI");
    var internalLine=formatter.Format(ConnectionLogFormatter.Parse(Raw(inbound:"limited-learning-proxy"))!,[]);
    Check(internalLine.Contains("专用代理入口，固定使用默认策略") && !internalLine.Contains("非原应用连接"),"Dedicated inlet misclassified as learning-only traffic");
    var toolOwner=owner with { Name="gh.exe",Remote=new(IPAddress.Loopback,17881) };
    var toolLine=formatter.Format(ConnectionLogFormatter.Parse(Raw(inbound:"limited-learning-proxy"))!,[toolOwner]);
    Check(toolLine.Contains("gh.exe (PID 42)") && toolLine.Contains("节点:日常香港"),"Explicit tool proxy request lost its process or node");
    return Task.CompletedTask;
});
await Test("Asynchronous diagnostics preserve entries on query failure and bounded queue overflow", async () =>
{
    var lines=new ConcurrentQueue<string>();
    var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using (var session=new ConnectionLogSession(formatter,s=>{lines.Enqueue(s);return Task.CompletedTask;},async(_,ct)=>{await gate.Task.WaitAsync(ct);throw new IOException("fixture");}))
    {
        var timer=Stopwatch.StartNew();
        for(var i=0;i<600;i++)session.Post(Raw());
        session.Post("original error preserved");
        Check(timer.Elapsed<TimeSpan.FromSeconds(1),"Post waited for lookup");
        gate.SetResult();
    }
    Check(lines.Count==601,"Log lines dropped");
    Check(lines.Any(x=>x.Contains("系统连接表查询失败")),"Lookup failure not labeled");
    Check(lines.Any(x=>x.Contains("日志查询繁忙")),"Overflow fallback absent");
    Check(lines.Contains("original error preserved"),"Original diagnostics lost");
});
await Test("Stopping diagnostics remains bounded even if the display callback hangs", async () =>
{
    var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var blocked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var session=new ConnectionLogSession(formatter,_=>{entered.TrySetResult();return blocked.Task;},(_,_)=>Task.FromResult<IReadOnlyList<TcpProcessOwner>>([]));
    session.Post(Raw());
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var timer=Stopwatch.StartNew();
    await session.DisposeAsync();
    blocked.SetResult();
    Check(timer.Elapsed<TimeSpan.FromSeconds(4),"Logging blocked core shutdown");
});
if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
    await Test("Real local TCP connection resolves to this process for IPv4 and IPv6", async () =>
    {
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            using var listener=new TcpListener(address,0);listener.Start();
            var port=((IPEndPoint)listener.LocalEndpoint).Port;
            using var client=new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address,port);
            using var accepted=await listener.AcceptTcpClientAsync();
            var rows=await TcpProcessSnapshot.CaptureAsync(new HashSet<int>{port},CancellationToken.None);
            var match=TcpProcessSnapshot.Find(rows,(IPEndPoint)client.Client.LocalEndPoint!,port);
            Check(match?.Pid==Environment.ProcessId && !string.IsNullOrEmpty(match.Name),"Native socket owner incorrect for "+address);
        }
    });
Console.WriteLine($"{count} connection logging checks passed.");
