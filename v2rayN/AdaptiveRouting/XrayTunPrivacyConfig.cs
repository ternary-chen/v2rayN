using System.Net;
using System.Text.Json.Nodes;

namespace AdaptiveRouting;

/// <summary>Shared private DNS and native Xray TUN guardrails. Applied after routing so that
/// DNS transport cannot be captured by user rules or the direct-first learner.</summary>
public static class XrayTunPrivacyConfig
{
    public const string DnsTag = "tun-private-dns";
    public const string BootstrapTag = "tun-bootstrap-dns";
    public const string DirectProbeTag = "limited-learning-direct";

    public static string Apply(string json, IEnumerable<string> proxyDomains, string bootstrap, bool windows)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var tun = root["inbounds"]!.AsArray().SingleOrDefault(x => x?["protocol"]?.ToString() == "tun");
        if (tun != null)
        {
            var settings = tun["settings"]!.AsObject();
            // Always capture both families, including a host which acquires IPv6 after startup.
            settings["autoSystemRoutingTable"] = new JsonArray("0.0.0.0/0", "::/0");
            var gateways = settings["gateway"]!.AsArray();
            if (!gateways.Any(x => x!.ToString().Contains(':')))
                gateways.Add("fdfe:dcba:9876::1/126");
            settings["dns"] = new JsonArray("1.1.1.1", "2606:4700:4700::1111");
            if (windows) settings["autoSystemWfpBlockLeak"] = new JsonArray("dns", "misconfigtun");
            foreach (var inbound in root["inbounds"]!.AsArray().Where(x => x?["tag"]?.ToString() != XrayLearningConfig.ForcedProxyTag && x?["tag"]?.ToString() != DirectProbeTag))
                inbound!["sniffing"] = new JsonObject
                {
                    ["enabled"] = true, ["destOverride"] = new JsonArray("http", "tls", "quic", "fakedns"),
                    ["routeOnly"] = false
                };

        }

        var endpoints = proxyDomains.Select(x => x.StartsWith("full:") ? x : "full:" + x).Distinct().ToArray();
        var servers = new JsonArray();
        if (endpoints.Length > 0)
        {
            // Only proxy endpoint names may bootstrap outside the proxy. Never use a
            // hostname here: resolving a resolver through itself can deadlock startup.
            if (!IPAddress.TryParse(bootstrap.Trim(), out _))
                throw new ArgumentException("启动 DNS 必须填写单个 IP 地址；仅用于解析代理服务器域名。");
            servers.Add(new JsonObject
            {
                ["address"] = bootstrap.Trim(), ["tag"] = BootstrapTag,
                ["domains"] = new JsonArray(endpoints.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["skipFallback"] = true
            });
        }
        if (tun != null) servers.Add("fakedns");
        // IP-literal DoH endpoint avoids a system-DNS bootstrap for the private resolver.
        servers.Add(new JsonObject { ["address"] = "https://1.1.1.1/dns-query", ["tag"] = DnsTag });
        root["dns"] = new JsonObject
        {
            ["servers"] = servers, ["tag"] = DnsTag, ["disableFallbackIfMatch"] = true,
            ["queryStrategy"] = "UseIP", ["enableParallelQuery"] = false,
            ["hosts"] = root["dns"]?["hosts"]?.DeepClone()
        };
        if (tun != null) root["fakedns"] = new JsonArray(
            new JsonObject { ["ipPool"] = "198.18.0.0/15", ["poolSize"] = 65535 },
            new JsonObject { ["ipPool"] = "fc00::/18", ["poolSize"] = 65535 });

        var outbounds = root["outbounds"]!.AsArray();
        // Direct destinations must also resolve in the core; Freedom's default AsIs
        // delegates hostname lookups to the OS and defeats DNS containment.
        foreach (var direct in outbounds.Where(x => x?["protocol"]?.ToString() == "freedom"))
        {
            direct!["settings"]?.AsObject().Remove("domainStrategy");
            direct["streamSettings"] ??= new JsonObject();
            direct["streamSettings"]!["sockopt"] ??= new JsonObject();
            direct["streamSettings"]!["sockopt"]!["domainStrategy"] = "ForceIP";
        }
        var dns = outbounds.SingleOrDefault(x => x?["protocol"]?.ToString() == "dns");
        if (dns == null)
        {
            dns = new JsonObject { ["protocol"] = "dns", ["tag"] = "dns-out" };
            outbounds.Add(dns);
        }
        // Default A/AAAA hijack; other record types return empty instead of being
        // forwarded to the application's chosen plaintext resolver (Xray 26.10.10+).
        dns["settings"] = new JsonObject
        {
            ["rules"] = new JsonArray(
                new JsonObject { ["qType"] = "1,28", ["action"] = "hijack" },
                new JsonObject { ["action"] = "return", ["rCode"] = 0 })
        };
        var rules = root["routing"]!["rules"]!.AsArray();
        var guards = new List<JsonObject>
        {
            Tagged("tun-dns-transport", DnsTag, "proxy"),
            Tagged("tun-dns-bootstrap", BootstrapTag, "direct"),
            new() { ["type"] = "field", ["ruleTag"] = "tun-dns-intercept", ["network"] = "tcp,udp", ["port"] = "53", ["outboundTag"] = dns["tag"]!.ToString() },
        };
        if (tun != null)
        {
            // Preserve calls instead of rejecting STUN. A UDP-incapable proxy fails
            // closed; never add a direct fallback. Ports alone are not exhaustive.
            guards.Add(new JsonObject { ["type"] = "field", ["ruleTag"] = "tun-stun-udp", ["network"] = "udp",
                ["port"] = "3478,5349,19302-19309", ["outboundTag"] = "proxy" });
            // Domain/AI/community and explicit manual choices still win for QUIC.
            // Unknown browser datagrams must not fall through to geo-IP/direct rules.
            var browserPosition = 0;
            for (var i = 0; i < rules.Count; i++)
            {
                var tag = rules[i]?["ruleTag"]?.ToString() ?? "";
                if (tag.StartsWith("manual-rule-") || tag.StartsWith("community-dedicated-") || tag == "ai-services")
                    browserPosition = i + 1;
            }
            rules.Insert(browserPosition, new JsonObject { ["type"] = "field", ["ruleTag"] = "tun-browser-udp", ["network"] = "udp",
                ["process"] = new JsonArray("chrome", "msedge", "firefox", "brave", "opera", "chromium"), ["outboundTag"] = "proxy" });
        }
        for (var i = guards.Count - 1; i >= 0; i--) rules.Insert(0, guards[i]);
        return root.ToJsonString();
    }

    private static JsonObject Tagged(string name, string inbound, string outbound) => new()
    {
        ["type"] = "field", ["ruleTag"] = name,
        ["inboundTag"] = new JsonArray(inbound), ["outboundTag"] = outbound
    };
}
