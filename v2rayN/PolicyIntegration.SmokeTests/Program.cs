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
    config.AiPolicy.Enabled = false;
    var disabled = await CoreConfigContextBuilder.BuildAll(config, normal2);
    Check(disabled.Success && disabled.MainResult.Context.AiPolicyNode is null, "Disabled policy still resolved AI node");
    Console.WriteLine("PASS Disabled dual policy leaves ordinary routing in control");
}
finally { await db.DisposeDbConnectionAsync(); }
