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
    config.AiPolicy = new() { Enabled = true, AiProfileId = ai.IndexId };
    var route = new RoutingItem { Id = id, Remarks = "fixture", IsActive = true, DomainStrategy = "AsIs", RuleSet = "[{\"OutboundTag\":\"direct\",\"Domain\":[\"domain:google.com\"]},{\"OutboundTag\":\"direct\",\"Network\":\"tcp,udp\"}]" };
    await db.InsertAsync(route);
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
    Check(ordered[0]!["inboundTag"]![0]!.ToString() == XrayLearningConfig.ForcedProxyTag, "Internal probe no longer bypasses manual routing");
    Check(ordered[1]!["process"] is JsonArray && ordered[1]!["outboundTag"]!.ToString() == "direct", "Protected programs lost precedence");
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
    Check(!(await CoreConfigContextBuilder.BuildAll(config, normal2)).Success, "Manual rules silently ignored TUN mode");
    config.TunModeItem.EnableTun = false;
    var unsupported = Node("singbox", "198.51.100.30"); unsupported.CoreType = ECoreType.sing_box;
    Check(!(await CoreConfigContextBuilder.BuildAll(config, unsupported)).Success, "Manual rules silently ignored unsupported core");
    Console.WriteLine("PASS Manual-only dedicated exit, reordering, missing-node/invalid-input rollback and unsupported-mode rejection");

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
