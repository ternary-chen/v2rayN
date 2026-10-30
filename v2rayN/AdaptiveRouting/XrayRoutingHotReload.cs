using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AdaptiveRouting;

/// <summary>Uses Xray's atomic AddRule replacement. Never owns or stops the proxy process.</summary>
public static class XrayRoutingHotReload
{
    private const string RevisionPrefix = "v2rayn-hot-revision:";
    private const string MarkerInbound = "v2rayn-hot-revision-unused";

    public static string Prepare(string json, int port)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        if (port is < 1024 or > 65535) throw new ArgumentException("热更新 API 端口无效。");
        root["api"] = new JsonObject
        {
            ["tag"] = "v2rayn-routing-api", ["listen"] = $"127.0.0.1:{port}",
            ["services"] = new JsonArray("RoutingService")
        };
        var rules = root["routing"]!["rules"]!.AsArray();
        if (root["inbounds"]!.AsArray().Any(x => x?["tag"]?.ToString() == MarkerInbound))
            throw new ArgumentException("入站名称与热更新保留名称冲突。");
        for (var i = rules.Count - 1; i >= 0; i--)
            if (rules[i]?["ruleTag"]?.ToString().StartsWith(RevisionPrefix, StringComparison.Ordinal) == true) rules.RemoveAt(i);
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rules.ToJsonString())));
        // An unreachable final rule identifies the complete ruleset, including match values.
        // ListRule returns only tags, so ordinary manual-rule-N tags cannot detect changed values.
        rules.Add(new JsonObject
        {
            ["type"] = "field", ["inboundTag"] = new JsonArray(MarkerInbound),
            ["outboundTag"] = "direct", ["ruleTag"] = RevisionPrefix + revision
        });
        return root.ToJsonString();
    }

    public static void EnsureRulesOnly(string before, string after)
    {
        var oldRoot = JsonNode.Parse(before)!;
        var newRoot = JsonNode.Parse(after)!;
        foreach (var root in new[] { oldRoot, newRoot })
        {
            if (root["api"]?["listen"]?.ToString().StartsWith("127.0.0.1:", StringComparison.Ordinal) != true ||
                root["routing"]?["rules"] is not JsonArray)
                throw new InvalidOperationException("当前内核没有本地路由热更新接口，请先正常启动支持热更新的版本。");
            if (!string.IsNullOrEmpty(root["routing"]?["script"]?.ToString()))
                throw new InvalidOperationException("脚本路由不支持此规则热更新。");
            root["routing"]!.AsObject().Remove("rules");
        }
        if (!JsonNode.DeepEquals(oldRoot, newRoot))
            throw new InvalidOperationException("此次修改包含节点、DNS、监听或其他非规则配置，不能无中断应用。请单独应用节点/配置变更后再保存规则。");
    }

    private static List<(string Tag, string RuleTag)> Expected(string config) => JsonNode.Parse(config)!["routing"]!["rules"]!.AsArray()
        .Select(x => (x?["outboundTag"]?.ToString() ?? "", x?["ruleTag"]?.ToString() ?? "")).ToList();

    public static async Task ApplyAsync(string executable, string assetsDirectory, string scratchDirectory,
        string before, string after, Func<bool> isSameProcessAlive, Func<Task> persist)
    {
        EnsureRulesOnly(before, after);
        if (!isSameProcessAlive()) throw new InvalidOperationException("原内核已停止，未更新规则。");
        var endpoint = JsonNode.Parse(before)!["api"]!["listen"]!.ToString();
        var expectedOld = Expected(before);
        var expectedNew = Expected(after);
        async Task<bool> Matches(List<(string Tag, string RuleTag)> expected)
        {
            if (!isSameProcessAlive()) return false;
            var response = await RunAsync(executable, assetsDirectory, ["api", "lsrules", "--server=" + endpoint, "--timeout=5"]);
            var actual = JsonNode.Parse(response)?["rules"]?.AsArray()
                .Select(x => (x?["tag"]?.ToString() ?? "", x?["ruleTag"]?.ToString() ?? "")).ToList();
            return isSameProcessAlive() && actual != null && expected.SequenceEqual(actual);
        }
        if (!await Matches(expectedOld))
            throw new InvalidOperationException("运行中的规则与上次应用快照不一致，未覆盖规则，也未重启内核。");
        if (expectedOld.SequenceEqual(expectedNew)) { await persist(); return; }

        Directory.CreateDirectory(scratchDirectory);
        var oldFile = Path.Combine(scratchDirectory, "rules-before-" + Guid.NewGuid().ToString("N") + ".json");
        var newFile = Path.Combine(scratchDirectory, "rules-after-" + Guid.NewGuid().ToString("N") + ".json");
        async Task Replace(string path)
        {
            if (!isSameProcessAlive()) throw new InvalidOperationException("内核已退出，未发送规则。");
            await RunAsync(executable, assetsDirectory, ["api", "adrules", "--server=" + endpoint, "--timeout=5", "--append=false", path]);
        }
        // Only routing is written to temporary files; node credentials are never included.
        static string RoutingOnly(string config) => new JsonObject { ["routing"] = JsonNode.Parse(config)!["routing"]!.DeepClone() }.ToJsonString();
        await File.WriteAllTextAsync(oldFile, RoutingOnly(before));
        await File.WriteAllTextAsync(newFile, RoutingOnly(after));
        var mutationAttempted = false;
        try
        {
            mutationAttempted = true;
            try { await Replace(newFile); }
            catch
            {
                // A lost reply does not prove failure. Reconcile before deciding to roll back.
                if (!await Matches(expectedNew)) throw;
            }
            if (!await Matches(expectedNew)) throw new IOException("热更新回读校验失败。");
            await persist();
        }
        catch (Exception error)
        {
            if (mutationAttempted)
            {
                try
                {
                    if (!await Matches(expectedOld))
                    {
                        await Replace(oldFile);
                        if (!await Matches(expectedOld)) throw new IOException("旧规则回读不匹配。");
                    }
                }
                catch (Exception rollback)
                {
                    throw new InvalidOperationException("规则更新失败，且无法确认旧规则已恢复；未重启内核。请检查内核状态。" + rollback.Message, error);
                }
            }
            throw new InvalidOperationException("规则未保存，旧规则已保留或恢复；内核未重启。" + error.Message, error);
        }
        finally
        {
            foreach (var file in new[] { oldFile, newFile })
                try { File.Delete(file); } catch (IOException) { }
        }
    }

    private static async Task<string> RunAsync(string executable, string assetsDirectory, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["XRAY_LOCATION_ASSET"] = assetsDirectory;
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Xray API 工具。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw new TimeoutException("Xray 规则 API 超时；正在核对运行规则。");
        }
        var stdout = await output;
        var stderr = await error;
        if (process.ExitCode != 0) throw new IOException("Xray 规则 API 拒绝更新：" + stderr + stdout);
        return stdout;
    }
}
