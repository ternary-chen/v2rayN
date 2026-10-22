using System.Text.Json;
// Isolated updater acceptance fixture. Never loads real profiles or changes system proxy settings.
var root = AppContext.BaseDirectory;
var control = Environment.GetEnvironmentVariable("UPDATE_FIXTURE_CONTROL") ?? root;
var config = Environment.GetEnvironmentVariable("UPDATE_FIXTURE_CONFIG") ?? Path.Combine(root, "guiConfigs");
if (!root.Contains("custom-update-test-", StringComparison.Ordinal)) return 90;
using var build = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "custom-build.json")));
var number = build.RootElement.GetProperty("Build").GetInt32();
var health = Environment.GetEnvironmentVariable("V2RAYN_UPDATE_HEALTH_FILE");
var nonce = Environment.GetEnvironmentVariable("V2RAYN_UPDATE_NONCE");
if (health != null)
{
    if (Environment.GetEnvironmentVariable("UPDATE_FIXTURE_FAIL") == "1" && int.TryParse(Environment.GetEnvironmentVariable("UPDATE_FIXTURE_BASE_BUILD"), out var originalBuild) && number > originalBuild)
    { await File.WriteAllTextAsync(Path.Combine(config, "guiNConfig.json"), "changed-by-failed-new-version"); return 12; }
    await File.WriteAllTextAsync(health, JsonSerializer.Serialize(new { Build = number, Nonce = nonce, Pid = Environment.ProcessId, Executable = Environment.ProcessPath }));
}
await File.WriteAllTextAsync(Path.Combine(control, "fixture-pid"), Environment.ProcessId.ToString());
var end = DateTime.UtcNow.AddMinutes(3);
while (DateTime.UtcNow < end)
{
    var stop = Path.Combine(control, "stop-pid");
    if (File.Exists(stop) && (await File.ReadAllTextAsync(stop)).Trim() == Environment.ProcessId.ToString()) break;
    await Task.Delay(50);
}
return 0;
