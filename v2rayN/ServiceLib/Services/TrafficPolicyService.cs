using AdaptiveRouting;
using ServiceLib.Handler.Builder;

namespace ServiceLib.Services;

public sealed record TrafficPolicyChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

public static class TrafficPolicyService
{
    public static bool IsSupportedNode(ProfileItem node) =>
        MihomoBridgeService.IsNode(node) ||
        (Global.XraySupportConfigType.Contains(node.ConfigType) && !node.ConfigType.IsComplexType());

    public static async Task<IReadOnlyList<TrafficPolicyChoice>> ChoicesAsync()
    {
        var subs = (await AppManager.Instance.SubItems() ?? []).ToDictionary(x => x.Id, x => x.Remarks);
        return (await AppManager.Instance.ProfileItems("") ?? []).Where(IsSupportedNode)
            .Select(x => new TrafficPolicyChoice(x.IndexId, $"[{(subs.GetValueOrDefault(x.Subid ?? "") ?? "本地节点")}] {x.Remarks}"))
            .OrderBy(x => x.Label).ToArray();
    }

    private static readonly SemaphoreSlim SaveGate = new(1, 1);

    public static Task SaveAsync(Config config, bool enabled, string? aiId, string? defaultId,
        IEnumerable<ManualRouteRule>? manualRules = null, IEnumerable<string>? directProcesses = null, bool? communityPolicy = null) =>
        SaveInternalAsync(config, enabled, aiId, defaultId, manualRules, directProcesses, false, false, communityPolicy);

    // Returns true only for an explicitly requested node change requiring the caller to reload.
    public static Task<bool> SaveAndApplyAsync(Config config, bool enabled, string? aiId, string? defaultId,
        IEnumerable<ManualRouteRule> manualRules, IEnumerable<string> directProcesses, bool allowNodeRestart = false, bool? communityPolicy = null) =>
        SaveInternalAsync(config, enabled, aiId, defaultId, manualRules, directProcesses, true, allowNodeRestart, communityPolicy);

    private static async Task<bool> SaveInternalAsync(Config config, bool enabled, string? aiId, string? defaultId,
        IEnumerable<ManualRouteRule>? manualRules, IEnumerable<string>? directProcesses, bool apply, bool allowNodeRestart, bool? communityPolicy)
    {
        await SaveGate.WaitAsync();
        try
        {
            var candidate = JsonUtils.DeepCopy(config);
            candidate.AiPolicy = new AiPolicyOptions { Enabled = enabled, AiProfileId = aiId ?? "",
                CommunityProxyToDedicated = communityPolicy ?? config.AiPolicy?.CommunityProxyToDedicated ?? true };
            candidate.IndexId = defaultId ?? config.IndexId;
            candidate.ManualRules = XrayManualRoutingConfig.Normalize(manualRules ?? candidate.ManualRules ?? []);
            if (directProcesses != null)
            {
                candidate.LimitedLearning ??= new();
                candidate.LimitedLearning.DirectProcesses = directProcesses.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
                candidate.LimitedLearning.Validate();
            }
            var nodesChanged = candidate.IndexId != config.IndexId || candidate.AiPolicy.AiProfileId != (config.AiPolicy?.AiProfileId ?? "");
            if (apply && nodesChanged && !allowNodeRestart)
                throw new InvalidOperationException("节点选择发生变化。仅保存规则不会重启内核；如需切换节点，请点击“应用节点变更并重启”。");
            CoreConfigContext? context = null;
            if (apply || enabled || candidate.AiPolicy.CommunityProxyToDedicated || candidate.ManualRules.Any(x => x.Enabled))
            {
                if (string.IsNullOrWhiteSpace(candidate.IndexId) || ((enabled || candidate.AiPolicy.CommunityProxyToDedicated || candidate.ManualRules.Any(x => x.Enabled && x.Target == "ai-policy")) && string.IsNullOrWhiteSpace(aiId)))
                    throw new InvalidOperationException("请选择默认代理节点；启用专线规则时还需选择专线节点。");
                var node = await AppManager.Instance.GetProfileItem(candidate.IndexId)
                    ?? throw new InvalidOperationException("默认代理节点已被删除，请重新选择。");
                var built = await CoreConfigContextBuilder.BuildAll(candidate, node);
                if (!built.Success) throw new InvalidOperationException(string.Join("；", built.CombinedValidatorResult.Errors));
                context = built.MainResult.Context;
                var generated = await CoreConfigHandler.GenerateClientConfig(context, null);
                if (!generated.Success) throw new InvalidOperationException(generated.Msg);
            }
            var file = Utils.GetConfigPath("guiNConfig.json");
            if (File.Exists(file)) File.Copy(file, Utils.GetBackupPath($"before-traffic-policies-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"));
            async Task Persist()
            {
                if (await ConfigHandler.SaveConfig(candidate) != 0) throw new IOException("规则保存失败，已保留原设置。");
                config.AiPolicy = candidate.AiPolicy;
                config.IndexId = candidate.IndexId;
                config.ManualRules = candidate.ManualRules;
                config.LimitedLearning = candidate.LimitedLearning;
            }
            if (apply && !nodesChanged)
            {
                await CoreManager.Instance.ApplyRulesAsync(context!, Persist);
                return false;
            }
            await Persist();
            return apply && nodesChanged;
        }
        finally { SaveGate.Release(); }
    }
}
