using AdaptiveRouting;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Handler.Builder;
using ServiceLib.Helper;
using ServiceLib.Manager;
using ServiceLib.Models.Entities;
using ServiceLib.Services;
using System.Text.Json.Nodes;

void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
// Never select the user's app-data path or start/stop a core/system proxy in this check.
Environment.SetEnvironmentVariable(Global.LocalAppData, "0");
Check(Path.GetFullPath(Utils.StartupPath()).TrimEnd(Path.DirectorySeparatorChar) == Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), "Unexpected test storage path");
Check(AppContext.BaseDirectory.Contains("PolicyIntegration.SmokeTests"), "Tests must use their isolated build directory");
Check(AppManager.Instance.InitApp(), "Fixture initialization failed");
var db = SQLiteHelper.Instance;
try
{
    var config = AppManager.Instance.Config;
    var id = Guid.NewGuid().ToString("N");
    ProfileItem Node(string name, string address) => new() { IndexId = id + name, Remarks = "same-display-name", ConfigType = EConfigType.SOCKS, CoreType = ECoreType.Xray, Address = address, Port = 1080 };
    var normal = Node("normal", "198.51.100.10");
    var normal2 = Node("normal2", "198.51.100.11");
    var ai = Node("ai", "198.51.100.20");
    await db.InsertAllAsync(new[] { normal, normal2, ai });
    config.IndexId = normal.IndexId;
    config.TunModeItem.EnableTun = false;
    config.LimitedLearning.Enabled = true;
    config.AiPolicy = new() { Enabled = true, AiProfileId = ai.IndexId, CommunityProxyToDedicated = false };
    var route = new RoutingItem { Id = id, Remarks = "fixture", IsActive = true, DomainStrategy = "AsIs", RuleSet = "[{\"OutboundTag\":\"direct\",\"Domain\":[\"domain:google.com\"]},{\"OutboundTag\":\"direct\",\"Network\":\"tcp,udp\"}]" };
    await db.ExecuteAsync("UPDATE RoutingItem SET IsActive = 0");
    await db.InsertAsync(route);
    await ConfigHandler.SetDefaultRouting(config, route);
    async Task<JsonNode> Generate(ProfileItem node)
    {
        var built = await CoreConfigContextBuilder.BuildAll(config, node);
        Check(built.Success, string.Join(";", built.CombinedValidatorResult.Errors));
        Check(built.MainResult.Context.AiPolicyNode?.IndexId == ai.IndexId, "AI node resolved by display name rather than ID");
        var generated = await CoreConfigHandler.GenerateClientConfig(built.MainResult.Context, null);
        Check(generated.Success, generated.Msg ?? "Configuration generation failed");
        return JsonNode.Parse(generated.Data!.ToString()!)!;
    }
    string Address(JsonNode json, string tag) => json["outbounds"]!.AsArray().Single(x => x?["tag"]?.ToString() == tag)!["settings"]!["address"]!.ToString();
    var result = await Generate(normal);

    Check(Address(result, "proxy") == normal.Address && Address(result, "ai-policy") == ai.Address, "Policy exits mixed up");
    Console.WriteLine("PASS Real config builder generates independent AI/default nodes with duplicate display names");
    await TrafficPolicyService.SaveAsync(config, true, ai.IndexId, normal2.IndexId);
    result = await Generate(normal2);
    Check(Address(result, "proxy") == normal2.Address && Address(result, "ai-policy") == ai.Address, "Changing default changed AI node");
    Console.WriteLine("PASS Saving default selection preserves AI selection and produces the new default exit");
    var before = File.ReadAllBytes(Utils.GetConfigPath("guiNConfig.json"));
    try { await TrafficPolicyService.SaveAsync(config, true, "missing-ai-id", normal.IndexId); throw new Exception("Missing AI accepted"); }
    catch (InvalidOperationException) { }
    Check(config.IndexId == normal2.IndexId && config.AiPolicy.AiProfileId == ai.IndexId, "Failed save changed current choices");
    Check(File.ReadAllBytes(Utils.GetConfigPath("guiNConfig.json")).SequenceEqual(before), "Failed validation rewrote configuration");
    Console.WriteLine("PASS Invalid AI selection preserves saved configuration and active selections");
    // Exercise the real save and core-generation paths, with isolated synthetic nodes.
    var manual = new List<ManualRouteRule>
    {
        new() { Kind = "domain", Value = "api.openai.com", Target = "direct", Remarks = "override AI" },
        new() { Kind = "domain-suffix", Value = "openai.com", Target = "proxy" },
        new() { Kind = "domain-suffix", Value = "example.org", Target = "ai-policy" },
        new() { Kind = "domain", Value = "disabled.example.org", Target = "direct", Enabled = false },
        new() { Kind = "ip", Value = "2001:db8::/32", Target = "direct" },
        new() { Kind = "process", Value = "example.exe", Target = "proxy" },
        new() { Kind = "process-dir", Value = "D:/apps/example", Target = "direct" }
    };
    await TrafficPolicyService.SaveAsync(config, true, ai.IndexId, normal2.IndexId, manual);
    result = await Generate(normal2);
    var ordered = result["routing"]!["rules"]!.AsArray();
    int RuleIndex(string tag) => ordered.Select((x, i) => (x, i)).Single(x => x.x?["ruleTag"]?.ToString() == tag).i;
    Check(RuleIndex("manual-rule-1") < RuleIndex("manual-rule-2") && RuleIndex("manual-rule-2") < RuleIndex("ai-services"), "Manual precedence/order lost");
    Check(ordered[RuleIndex("manual-rule-1")]!["domain"]![0]!.ToString() == "full:api.openai.com" && ordered[RuleIndex("manual-rule-1")]!["outboundTag"]!.ToString() == "direct", "AI direct override changed");
    Check(ordered[RuleIndex("manual-rule-2")]!["outboundTag"]!.ToString() == "proxy", "AI default-proxy override changed");
    Check(ordered[RuleIndex("manual-rule-3")]!["outboundTag"]!.ToString() == "ai-policy", "Manual dedicated rule has wrong target");
    Check(!ordered.ToJsonString().Contains("disabled.example.org"), "Disabled manual rule was generated");
    Check(ordered.Take(RuleIndex("manual-rule-1")).Any(x => x?["inboundTag"]?[0]?.ToString() == XrayLearningConfig.ForcedProxyTag), "Internal probe no longer bypasses manual routing");
    Check(ordered.Take(RuleIndex("manual-rule-1")).Any(x => x?["process"] is JsonArray && x?["outboundTag"]?.ToString() == "direct"), "Protected programs lost precedence");
    Check(ordered.Any(x => x?["process"]?[0]?.ToString() == "example"), "Windows process suffix not normalized");
    Check(ordered.Any(x => x?["process"]?[0]?.ToString() == "D:/apps/example/"), "Process directory not normalized");
    var restored = JsonUtils.Deserialize<ServiceLib.Models.Configs.Config>(File.ReadAllText(Utils.GetConfigPath("guiNConfig.json")))!;
    Check(restored.ManualRules.Count == manual.Count && !restored.ManualRules[3].Enabled && restored.ManualRules[0].Remarks == "override AI", "Rule round trip lost order, state or remarks");
    Console.WriteLine("PASS Manual AI overrides, dedicated/default/direct exits, disabled rules, protected programs and config round trip");

    // Changing one rule's order must affect the actual generated first match.
    (manual[0], manual[1]) = (manual[1], manual[0]);
    await TrafficPolicyService.SaveAsync(config, true, ai.IndexId, normal2.IndexId, manual);
    result = await Generate(normal2);
    Check(result["routing"]!["rules"]!.AsArray().First(x => x?["ruleTag"]?.ToString() == "manual-rule-1")!["outboundTag"]!.ToString() == "proxy", "Reordering did not change the first match");

    await TrafficPolicyService.SaveAsync(config, false, ai.IndexId, normal2.IndexId, manual);
    result = await Generate(normal2);
    Check(!result["routing"]!.ToJsonString().Contains("ai-services") && Address(result, "ai-policy") == ai.Address, "Manual dedicated routing depends on built-in AI rules");
    before = File.ReadAllBytes(Utils.GetConfigPath("guiNConfig.json"));
    try { await TrafficPolicyService.SaveAsync(config, false, "missing-ai-id", normal2.IndexId, manual); throw new Exception("Missing manual dedicated node accepted"); }
    catch (InvalidOperationException) { }
    Check(File.ReadAllBytes(Utils.GetConfigPath("guiNConfig.json")).SequenceEqual(before) && config.AiPolicy.AiProfileId == ai.IndexId, "Missing dedicated node modified live or disk config");
    foreach (var bad in new[]
    {
        new ManualRouteRule { Value = "https://example.com/path" },
        new ManualRouteRule { Kind = "ip", Value = "10.0.0.0/99" },
        new ManualRouteRule { Kind = "process-dir", Value = "relative/path" },
        new ManualRouteRule { Value = "example.com", Target = "misspelled-policy" }
    })
    {
        try { await TrafficPolicyService.SaveAsync(config, false, ai.IndexId, normal2.IndexId, [bad]); throw new Exception("Invalid manual rule accepted"); }
        catch (ArgumentException) { }
        Check(File.ReadAllBytes(Utils.GetConfigPath("guiNConfig.json")).SequenceEqual(before), "Invalid input rewrote configuration");
    }
    config.TunModeItem.EnableTun = true;
    config.TunModeItem.EnableLegacyProtect = true;
    config.TunModeItem.AutoRoute = true;
    config.TunModeItem.StrictRoute = false;
    config.TunModeItem.EnableIPv6Address = false;
    config.AiPolicy.Enabled = true;
    config.SimpleDNSItem.BootstrapDNS = "119.29.29.29";
    var tunBuilt = await CoreConfigContextBuilder.BuildAll(config, normal2);
    Check(tunBuilt.Success && tunBuilt.PreSocksResult is null && tunBuilt.MainResult.Context.IsTunEnabled,
        "TUN policies must use a single native Xray even with legacy protection selected");
    var tunGenerated = await CoreConfigHandler.GenerateClientConfig(tunBuilt.MainResult.Context, null);
    Check(tunGenerated.Success, tunGenerated.Msg ?? "Native TUN generation failed");
    var tunJson = JsonNode.Parse(tunGenerated.Data!.ToString()!)!;
    Check(Address(tunJson, "proxy") == normal2.Address && Address(tunJson, "ai-policy") == ai.Address, "TUN mixed policy exits");
    var tunInbound = tunJson["inbounds"]!.AsArray().Single(x => x?["protocol"]?.ToString() == "tun")!;
    Check(tunInbound["settings"]!["autoSystemRoutingTable"]!.AsArray().Any(x => x!.ToString() == "::/0"), "IPv6 bypasses TUN when toggle is off");
    Check(tunInbound["settings"]!["gateway"]!.AsArray().Any(x => x!.ToString().Contains(':')), "Missing IPv6 TUN address");
    if (OperatingSystem.IsWindows())
        Check(tunInbound["settings"]!["autoSystemWfpBlockLeak"]!.AsArray().Count == 2, "Windows leak protection not enforced");
    Check(tunInbound["sniffing"]!["destOverride"]!.AsArray().Any(x => x!.ToString() == "fakedns") && tunInbound["sniffing"]!["routeOnly"]!.ToString() == "false", "Fake IP not restored before domain routing");
    Check(tunJson["dns"]!["servers"]!.AsArray().Count == 2, "Website DNS may fall back to ISP");
    Check(tunJson["dns"]!.ToJsonString().Contains("https://1.1.1.1/dns-query") && !tunJson["dns"]!.ToJsonString().Contains("119.29.29.29"), "Numeric proxy endpoints unexpectedly use bootstrap DNS");
    var tunRules = tunJson["routing"]!["rules"]!.AsArray();
    int TIndex(string tag) => tunRules.Select((x, i) => (x, i)).Single(x => x.x?["ruleTag"]?.ToString() == tag).i;
    Check(TIndex("tun-dns-intercept") < TIndex("manual-rule-1"), "Manual process/direct rules can bypass DNS capture");
    Check(TIndex("tun-browser-udp") > TIndex("ai-services") && tunRules[TIndex("tun-browser-udp")]!["outboundTag"]!.ToString() == "proxy", "Browser UDP breaks policy order or calls");
    Check(TIndex("manual-rule-1") < TIndex("ai-services"), "TUN changed manual versus AI precedence");
    Check(tunRules.Any(x => x?["inboundTag"]?[0]?.ToString() == XrayTunPrivacyConfig.DirectProbeTag && x?["outboundTag"]?.ToString() == "direct"), "TUN learner direct probes can recurse");
    Check(tunJson["inbounds"]!.AsArray().Any(x => x?["port"]?.ToString() == config.LimitedLearning.TunDirectPort.ToString()), "No loopback direct probe listener");
    var beforeTunRule = tunGenerated.Data!.ToString()!;
    config.ManualRules.Add(new() { Value = "new.example.org", Target = "proxy" });
    var updatedTun = await Generate(normal2);
    XrayRoutingHotReload.EnsureRulesOnly(beforeTunRule, updatedTun.ToJsonString());
    config.ManualRules.RemoveAt(config.ManualRules.Count - 1);
    config.TunModeItem.RouteExcludeAddress = ["0.0.0.0/0"];
    Check(!(await CoreConfigContextBuilder.BuildAll(config, normal2)).Success, "TUN accepted a route bypass");
    config.TunModeItem.RouteExcludeAddress = null;
    config.TunModeItem.AutoRoute = false;
    Check(!(await CoreConfigContextBuilder.BuildAll(config, normal2)).Success, "TUN accepted disabled capture");
    config.TunModeItem.AutoRoute = true;
    // Isolated synthetic fixture for xray -test and loopback DNS/UDP checks; no real credentials.
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "native-tun-fixture.json"), beforeTunRule);
    Console.WriteLine("PASS Native TUN dual exits, rule order, IPv6/WFP, DNS capture, browser UDP guard, probe isolation and hot reload");
    config.TunModeItem.EnableTun = false;
    config.AiPolicy.Enabled = false;
    var unsupported = Node("singbox", "198.51.100.30"); unsupported.CoreType = ECoreType.sing_box;
    Check(!(await CoreConfigContextBuilder.BuildAll(config, unsupported)).Success, "Manual rules silently ignored unsupported core");
    Console.WriteLine("PASS Manual-only dedicated exit, reordering, missing-node/invalid-input rollback and unsupported-mode rejection");

    // User-requested four overrides survive in BOTH transport modes and beat the community list.
    var originalRoute = route.RuleSet;
    route.RuleSet = "[{\"OutboundTag\":\"direct\",\"Domain\":[\"domain:known-proxy.example\"]},{\"OutboundTag\":\"proxy\",\"Domain\":[\"domain:known-proxy.example\",\"domain:yahoo.com\"]},{\"OutboundTag\":\"direct\",\"Network\":\"tcp,udp\"}]";
    await db.UpdateAsync(route);
    var requested = new[] { "pingip.co", "cleanip.io", "ip.net.coffee", "yahoo.com" };
    config.AiPolicy.Enabled = true;
    await TrafficPolicyService.SaveAsync(config, true, ai.IndexId, normal2.IndexId,
        requested.Select(x => new ManualRouteRule { Value = x, Target = "proxy" }), communityPolicy: true);
    foreach (var tunMode in new[] { false, true })
    {
        config.TunModeItem.EnableTun = tunMode;
        var split = await Generate(normal2);
        var sr = split["routing"]!["rules"]!.AsArray();
        var overrides = sr.Where(x => x?["ruleTag"]?.ToString().StartsWith("manual-rule-") == true).ToArray();
        Check(overrides.Select(x => x!["domain"]![0]!.ToString()).SequenceEqual(requested.Select(x => "domain:" + x)), "Screenshot rules changed order or disappeared");
        Check(overrides.All(x => x!["outboundTag"]!.ToString() == "proxy"), "Screenshot override rerouted to dedicated exit");
        var community = sr.First(x => x?["ruleTag"]?.ToString().StartsWith("community-dedicated-") == true)!;
        Check(community["outboundTag"]!.ToString() == "ai-policy", "Community blacklist not sent to chosen dedicated node");
        Check(sr.IndexOf(community) > sr.IndexOf(overrides[^1]) && sr.IndexOf(community) > sr.IndexOf(sr.First(x => x?["ruleTag"]?.ToString() == "ai-services")), "Manual/AI/community precedence changed");
        Check(sr.IndexOf(community) < sr.IndexOf(sr.First(x => x?["ruleTag"]?.ToString() == "community-mainland")), "Mainland rule precedes blacklist");
        Check(sr.Any(x => x?["outboundTag"]?.ToString() == XrayLearningConfig.UnknownTag), "Opted-in unknown-site probes removed");
        Check(split["dns"]!["servers"]!.ToJsonString().Contains("https://1.1.1.1/dns-query") && !split["dns"]!["servers"]!.ToJsonString().Contains("119.29.29.29"), "DNS outside core proxy path");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, tunMode ? "native-tun-fixture.json" : "system-proxy-fixture.json"), split.ToJsonString());
    }
    config.TunModeItem.EnableTun = false;
    config.AiPolicy.CommunityProxyToDedicated = false;
    config.AiPolicy.Enabled = false;
    route.RuleSet = originalRoute;
    await db.UpdateAsync(route);
    Console.WriteLine("PASS Both modes retain all four screenshot rules and community/AI/dedicated/mainland/learning order");

    // With no AI rules and no enabled dedicated rule, a removed dedicated node is harmless.
    foreach (var rule in manual) rule.Enabled = false;
    await TrafficPolicyService.SaveAsync(config, false, "missing-ai-id", normal2.IndexId, manual);
    var dormant = await CoreConfigContextBuilder.BuildAll(config, normal2);
    Check(dormant.Success && dormant.MainResult.Context.AiPolicyNode is null, "Disabled dedicated rule still required node");
    var dormantGenerated = await CoreConfigHandler.GenerateClientConfig(dormant.MainResult.Context, null);
    Check(dormantGenerated.Success && !dormantGenerated.Data!.ToString()!.Contains("manual-rule-"), "Disabled rules still active");
    // A default/direct manual rule also works without a dedicated node.
    await TrafficPolicyService.SaveAsync(config, false, "missing-ai-id", normal2.IndexId, [new() { Value = "example.com", Target = "direct" }]);
    var directOnly = await CoreConfigContextBuilder.BuildAll(config, normal2);
    Check(directOnly.Success && directOnly.MainResult.Context.AiPolicyNode is null, "Direct-only rule required dedicated node");
    await TrafficPolicyService.SaveAsync(config, false, "", normal2.IndexId, []);
    Console.WriteLine("PASS Disabled/removed rules and direct-only rules do not require a dedicated node");
    config.AiPolicy.Enabled = false;
    var disabled = await CoreConfigContextBuilder.BuildAll(config, normal2);
    Check(disabled.Success && disabled.MainResult.Context.AiPolicyNode is null, "Disabled policy still resolved AI node");
    Console.WriteLine("PASS Disabled dual policy leaves ordinary routing in control");
    Check(!CoreInfoManager.Instance.GetCheckUpdateCoreTypes().Contains(ECoreType.v2rayN), "Custom GUI exposed to generic auto installer");
    Check(CoreInfoManager.Instance.GetCoreInfo(ECoreType.v2rayN)?.Url?.Contains("ternary-chen/v2rayN") == true, "Client update source still upstream");
    var legacy = await new UpdateService(config, (_, _) => Task.CompletedTask).CheckHasUpdateOnly(ECoreType.v2rayN, true);
    Check(!legacy.Success && legacy.Url is null, "Legacy GUI updater returned an install URL");
    Console.WriteLine("PASS Generic core updater cannot overwrite the custom GUI with upstream binaries");
}
finally { await db.DisposeDbConnectionAsync(); }
await ProbeDelegationCheck.Run();

XrayPrivacyPrerequisites.ValidateVersion("Xray 26.10.10 example");
foreach (var old in new[] { "Xray 25.10.10 old", "unrecognized" })
{
    try { XrayPrivacyPrerequisites.ValidateVersion(old); throw new Exception("Old/unknown core accepted"); }
    catch (InvalidOperationException) { }
}
Console.WriteLine("PASS Old/unknown core is rejected before stopping the existing service");
