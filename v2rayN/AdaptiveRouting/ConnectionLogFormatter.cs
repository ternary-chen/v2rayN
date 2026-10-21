using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AdaptiveRouting;

public sealed record XrayAccessEntry(string Raw, string Source, string Destination, string Inbound, string Outbound);
public sealed record LearningConnectionEvent(long Id, string Destination, ConnectionRoute? Route, string Stage);

/// <summary>Labels are captured from the config being launched, never the currently selected UI row.</summary>
public sealed class ConnectionLogFormatter(string defaultNode, string? aiNode, IReadOnlyDictionary<string, int> inboundPorts)
{
    private static readonly Regex AccessPattern = new(@"\bfrom (?<src>\S+) accepted (?<dst>\S+) \[(?<in>[^\]\r\n]+?)\s(?:->|>>|==>)\s(?<out>[^\]\r\n]+)\]", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private readonly string _defaultNode = Safe(defaultNode);
    private readonly string _aiNode = Safe(aiNode ?? "未知（未配置 AI 出口）");
    private readonly Dictionary<string, int> _inboundPorts = new(inboundPorts);
    public IReadOnlySet<int> Ports => _inboundPorts.Values.ToHashSet();

    public static string Safe(string value) => new(value.Where(c => !char.IsControl(c) && c is not '[' and not ']' and not '|').Take(160).ToArray());

    public static IReadOnlyDictionary<string, int> ReadInboundPorts(string json)
    {
        var ports = new Dictionary<string, int>();
        foreach (var entry in JsonNode.Parse(json)?["inbounds"]?.AsArray() ?? [])
            if (entry?["tag"]?.GetValue<string>() is { } tag && int.TryParse(entry?["port"]?.ToString(), out var port) && port is > 0 and <= 65535)
                ports.TryAdd(tag, port);
        return ports;
    }

    public static XrayAccessEntry? Parse(string raw)
    {
        if (raw.Length > 16384 || !raw.Contains(" accepted ", StringComparison.Ordinal)) return null;
        try
        {
            var match = AccessPattern.Match(raw);
            return match.Success ? new(raw.TrimEnd('\r', '\n'), match.Groups["src"].Value, match.Groups["dst"].Value,
                match.Groups["in"].Value, match.Groups["out"].Value) : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    public string Format(XrayAccessEntry entry, IReadOnlyList<TcpProcessOwner> owners, string? unavailableReason = null)
    {
        var process = "未知（连接已结束、权限不足或未匹配）";
        var source = entry.Source.StartsWith("tcp:", StringComparison.Ordinal) ? entry.Source[4..] : entry.Source;
        if (entry.Destination.StartsWith("udp:", StringComparison.Ordinal) || entry.Source.StartsWith("udp:", StringComparison.Ordinal))
            process = "未知（UDP 未提供可靠进程归属）";
        else if (IPEndPoint.TryParse(source, out var endpoint))
        {
            if (!IPAddress.IsLoopback(endpoint.Address)) process = "未知（非本机客户端）";
            else if (unavailableReason != null) process = "未知（" + Safe(unavailableReason) + "）";
            else if (_inboundPorts.TryGetValue(entry.Inbound, out var port) && TcpProcessSnapshot.Find(owners, endpoint, port) is { } owner)
                process = $"{(string.IsNullOrEmpty(owner.Name) ? "名称不可读" : Safe(owner.Name))} (PID {owner.Pid})";
        }
        var (policy, node) = entry.Outbound switch
        {
            "proxy" => ("默认策略", _defaultNode),
            "ai-policy" => ("AI 策略", _aiNode),
            "direct" => ("直连", "无（直连）"),
            "block" or "blocked" => ("阻止", "无（阻止）"),
            "limited-learning" => ("自动检测", "待检测，尚未选择出口"),
            _ => ("其他出口 " + Safe(entry.Outbound), "未知（未映射节点）")
        };
        var role = entry.Inbound == "limited-learning-proxy" ? " | 用途:学习内部探测/转发，非原应用连接" : "";
        return $"{entry.Raw} [进程:{process} | 策略:{policy} | 节点:{node} | 状态:路由选择{role}]";
    }

    public string FormatLearning(LearningConnectionEvent entry)
    {
        var policy = entry.Route switch { ConnectionRoute.Proxy => "默认策略", ConnectionRoute.Direct => "直连", _ => "自动检测" };
        var node = entry.Route switch { ConnectionRoute.Proxy => _defaultNode, ConnectionRoute.Direct => "无（直连）", _ => "待检测，尚未选择出口" };
        return $"[学习连接#{entry.Id}] {Safe(entry.Destination)} [进程:未知（内部转发未携带原进程） | 策略:{policy} | 节点:{node} | 状态:{Safe(entry.Stage)}]";
    }
}
