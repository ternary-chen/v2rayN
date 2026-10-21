using System.Text.Json.Nodes;

namespace AdaptiveRouting;

public static class XrayAiPolicyConfig
{
    public const string OutboundTag = "ai-policy";

    public static string Apply(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var outbounds = root["outbounds"]?.AsArray() ?? throw new InvalidOperationException("缺少代理出口。");
        if (outbounds.Count(x => x?["tag"]?.GetValue<string>() == OutboundTag) != 1 ||
            outbounds.Count(x => x?["tag"]?.GetValue<string>() == "proxy") != 1)
            throw new InvalidOperationException("AI 策略与默认策略各需一个明确出口，请选择标准 Xray 单节点。");
        var rules = root["routing"]?["rules"]?.AsArray() ?? throw new InvalidOperationException("缺少路由规则。");
        if (rules.Any(x => x?["ruleTag"]?.GetValue<string>() == "ai-services"))
            throw new InvalidOperationException("AI 策略规则已存在，请勿重复应用。");
        // Learning is added afterwards: protected processes precede this rule, unknown probes use proxy.
        rules.Insert(0, new JsonObject
        {
            ["type"] = "field", ["ruleTag"] = "ai-services", ["outboundTag"] = OutboundTag,
            ["domain"] = new JsonArray(AiDomainCatalog.Domains.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray())
        });
        return root.ToJsonString();
    }
}
