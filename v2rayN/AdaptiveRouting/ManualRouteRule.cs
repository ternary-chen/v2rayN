using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AdaptiveRouting;

public sealed record RuleChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

public sealed class ManualRouteRule
{
    public bool Enabled { get; set; } = true;
    public string Kind { get; set; } = "domain-suffix";
    public string Value { get; set; } = "";
    public string Target { get; set; } = "proxy";
    public string Remarks { get; set; } = "";
    [JsonIgnore] public string KindLabel => Kinds.FirstOrDefault(x => x.Value == Kind)?.Label ?? Kind;
    [JsonIgnore] public string TargetLabel => Targets.FirstOrDefault(x => x.Value == Target)?.Label ?? Target;
    [JsonIgnore] public string StateLabel => Enabled ? "启用" : "停用";

    public static IReadOnlyList<RuleChoice> Kinds { get; } = new RuleChoice[]
    {
        new("domain-suffix", "域名及子域名"), new("domain", "完整域名"),
        new("keyword", "域名关键词"), new("ip", "IP / CIDR 网段"),
        new("geosite", "网站分类（geosite）"), new("geoip", "IP 分类（geoip）"),
        new("process", "进程名称"), new("process-dir", "程序目录")
    };
    public static IReadOnlyList<RuleChoice> Targets { get; } = new RuleChoice[]
    {
        new("proxy", "默认代理"), new("ai-policy", "专线"), new("direct", "强制直连")
    };

    public ManualRouteRule Copy() => new() { Enabled = Enabled, Kind = Kind, Value = Value, Target = Target, Remarks = Remarks };

    public ManualRouteRule Normalize()
    {
        var rule = Copy();
        rule.Value = rule.Value?.Trim() ?? "";
        rule.Remarks = rule.Remarks?.Trim() ?? "";
        if (!Kinds.Any(x => x.Value == rule.Kind) || !Targets.Any(x => x.Value == rule.Target))
            throw new ArgumentException("请选择有效的规则类型和出口策略。");
        if (rule.Value.Length is 0 or > 1024 || rule.Value.Any(char.IsControl))
            throw new ArgumentException("匹配内容不能为空、不能包含换行，且不得超过 1024 字符。");
        switch (rule.Kind)
        {
            case "domain":
            case "domain-suffix":
                try { rule.Value = new IdnMapping().GetAscii(rule.Value.TrimEnd('.')).ToLowerInvariant(); }
                catch (ArgumentException) { throw new ArgumentException("请输入域名，例如 example.com，不要包含网址协议、路径或通配符。"); }
                if (Uri.CheckHostName(rule.Value) != UriHostNameType.Dns || rule.Value.Contains('_') ||
                    rule.Value.Split('.').Any(x => x.Length is 0 or > 63 || x.StartsWith('-') || x.EndsWith('-')))
                    throw new ArgumentException("请输入域名，例如 example.com，不要包含网址协议、路径或通配符。");
                break;
            case "ip":
                var parts = rule.Value.Split('/');
                if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address))
                    throw new ArgumentException("请输入有效的 IPv4、IPv6 地址或 CIDR 网段。");
                if (parts.Length == 2 && (!int.TryParse(parts[1], out var bits) || bits < 0 || bits > (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128)))
                    throw new ArgumentException("CIDR 前缀长度无效。");
                rule.Value = address + (parts.Length == 2 ? "/" + parts[1] : "");
                break;
            case "geosite":
            case "geoip":
                if (rule.Value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '@' or '!')))
                    throw new ArgumentException("请只填分类名称，例如 openai 或 cn，不要包含 geosite: / geoip: 前缀。");
                rule.Value = rule.Value.ToLowerInvariant();
                break;
            case "keyword":
                if (rule.Value.Any(char.IsWhiteSpace) || rule.Value.IndexOfAny(['/', ':', '*']) >= 0)
                    throw new ArgumentException("请输入域名中的关键词，不要包含网址协议、路径或通配符。");
                break;
            case "process":
                if (rule.Value.IndexOfAny(['/', '\\', ':', '*', '?']) >= 0)
                    throw new ArgumentException("进程名称不能包含路径；程序文件夹请使用“程序目录”。");
                if (rule.Value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) rule.Value = rule.Value[..^4];
                if (string.IsNullOrWhiteSpace(rule.Value)) throw new ArgumentException("进程名称不能为空。");
                break;
            case "process-dir":
                rule.Value = rule.Value.Replace('\\', '/');
                if (!(rule.Value.StartsWith('/') || (rule.Value.Length > 2 && char.IsAsciiLetter(rule.Value[0]) && rule.Value[1..3] == ":/")) ||
                    rule.Value.IndexOfAny(['*', '?']) >= 0)
                    throw new ArgumentException("请填写绝对程序目录，例如 D:/apps/example/。");
                if (!rule.Value.EndsWith('/')) rule.Value += "/";
                break;
        }
        return rule;
    }
}

public static class XrayManualRoutingConfig
{
    public static List<ManualRouteRule> Normalize(IEnumerable<ManualRouteRule> rules) => rules.Select(x =>
        x?.Normalize() ?? throw new ArgumentException("规则不能为空。")).ToList();

    // Added before AI, then the learning transformer adds its internal entry and protected programs above it.
    public static string Apply(string json, IEnumerable<ManualRouteRule> rules)
    {
        var enabled = Normalize(rules).Where(x => x.Enabled).ToList();
        if (enabled.Count == 0) return json;
        var root = JsonNode.Parse(json)!.AsObject();
        var routing = root["routing"]!["rules"]!.AsArray();
        var outbounds = root["outbounds"]!.AsArray();
        var index = 0;
        foreach (var item in enabled)
        {
            if (outbounds.Count(x => x?["tag"]?.ToString() == item.Target) != 1)
                throw new InvalidOperationException($"手动规则“{item.Value}”的{item.TargetLabel}出口不存在或不唯一，请重新选择节点。");
            var rule = new JsonObject { ["type"] = "field", ["ruleTag"] = $"manual-rule-{index + 1}", ["outboundTag"] = item.Target };
            var (field, value) = item.Kind switch
            {
                "domain-suffix" => ("domain", "domain:" + item.Value),
                "domain" => ("domain", "full:" + item.Value),
                "keyword" => ("domain", "keyword:" + item.Value),
                "geosite" => ("domain", "geosite:" + item.Value),
                "geoip" => ("ip", "geoip:" + item.Value),
                "ip" => ("ip", item.Value),
                _ => ("process", item.Value)
            };
            rule[field] = new JsonArray(value);
            routing.Insert(index++, rule);
        }
        return root.ToJsonString();
    }
}
