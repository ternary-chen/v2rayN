using System.Text.Json;
using System.Text.Json.Nodes;

namespace AdaptiveRouting;

/// <summary>Leaves all explicit routing decisions ahead of learning. Only an unrestricted
/// final DIRECT rule, or the absence of a terminal rule, is treated as an unknown-site fallback.</summary>
public static class XrayLearningConfig
{
    public const string UnknownTag = "limited-learning";
    public const string ForcedProxyTag = "limited-learning-proxy";

    public static string Apply(string json, LearningOptions options)
    {
        options.Validate();
        var root = JsonNode.Parse(json)!.AsObject();
        var inbounds = root["inbounds"]!.AsArray();
        var outbounds = root["outbounds"]!.AsArray();
        var routing = root["routing"]!.AsObject();
        var rules = routing["rules"]!.AsArray();
        if (!options.Enabled)
        {
            AddProcessBypass(rules, options);
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        if (inbounds.Any(x => x?["protocol"]?.GetValue<string>() == "tun"))
            throw new InvalidOperationException("有限学习第一版仅支持非 TUN 模式。");
        if (outbounds.Any(x => x?["tag"]?.GetValue<string>() == UnknownTag) ||
            inbounds.Any(x => x?["tag"]?.GetValue<string>() == ForcedProxyTag))
            throw new InvalidOperationException("配置已包含学习端口，请勿重复应用。");
        if (inbounds.Any(x => x?["port"]?.ToString() is { } p &&
            (p == options.ListenerPort.ToString() || p == options.ProxyPort.ToString())))
            throw new InvalidOperationException("学习端口与现有入站端口冲突。");
        if (!outbounds.Any(x => x?["tag"]?.GetValue<string>() == "proxy") ||
            !outbounds.Any(x => x?["tag"]?.GetValue<string>() == "direct"))
            throw new InvalidOperationException("学习需要标准 proxy 和 direct 出站。");

        // Do not let default-IP-resolution hide the original hostname from the learner.
        // Explicit community rules still execute in their original order.
        var terminal = -1;
        for (var i = 0; i < rules.Count; i++)
        {
            if (!IsTerminal(rules[i]!.AsObject())) continue;
            if (rules[i]!["outboundTag"]?.GetValue<string>() != "direct")
                throw new InvalidOperationException("当前路由以全局代理/拦截结尾，请先选择默认直连的路由。");
            terminal = i;
            break;
        }
        if (terminal < 0) terminal = rules.Count;
        rules.Insert(terminal, new JsonObject
        {
            ["type"] = "field", ["network"] = "tcp", ["port"] = "80,443",
            ["outboundTag"] = UnknownTag
        });
        if (!rules.Any(x => IsTerminal(x!.AsObject())))
            rules.Add(new JsonObject { ["type"] = "field", ["network"] = "tcp,udp", ["outboundTag"] = "direct" });

        // Protected applications always win, including against broad community proxy domains.
        AddProcessBypass(rules, options);
        rules.Insert(0, new JsonObject
        {
            ["type"] = "field", ["inboundTag"] = new JsonArray(ForcedProxyTag), ["outboundTag"] = "proxy"
        });
        inbounds.Add(new JsonObject
        {
            ["tag"] = ForcedProxyTag, ["listen"] = "127.0.0.1", ["port"] = options.ProxyPort,
            ["protocol"] = "socks", ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = false }
        });
        outbounds.Add(new JsonObject
        {
            ["tag"] = UnknownTag, ["protocol"] = "socks",
            ["settings"] = new JsonObject
            {
                ["servers"] = new JsonArray(new JsonObject { ["address"] = "127.0.0.1", ["port"] = options.ListenerPort })
            }
        });
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void AddProcessBypass(JsonArray rules, LearningOptions options)
    {
        if (options.DirectProcesses.Count > 0)
            rules.Insert(0, new JsonObject
            {
                ["type"] = "field", ["process"] = JsonSerializer.SerializeToNode(options.DirectProcesses),
                ["outboundTag"] = "direct"
            });
    }

    public static bool IsTerminal(JsonObject rule)
    {
        foreach (var (key, value) in rule)
        {
            if (value is null || key is "type" or "outboundTag" or "ruleTag") continue;
            if (key == "network" && value.ToString() is "tcp,udp" or "udp,tcp") continue;
            if (key == "port" && value.ToString() is "0-65535" or "1-65535") continue;
            return false;
        }
        return true;
    }
}
