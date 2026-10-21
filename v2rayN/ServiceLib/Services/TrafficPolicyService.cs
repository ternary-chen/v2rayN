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
        Global.XraySupportConfigType.Contains(node.ConfigType) && !node.ConfigType.IsComplexType();

    public static async Task<IReadOnlyList<TrafficPolicyChoice>> ChoicesAsync()
    {
        var subs = (await AppManager.Instance.SubItems() ?? []).ToDictionary(x => x.Id, x => x.Remarks);
        return (await AppManager.Instance.ProfileItems("") ?? []).Where(IsSupportedNode)
            .Select(x => new TrafficPolicyChoice(x.IndexId, $"[{(subs.GetValueOrDefault(x.Subid ?? "") ?? "本地节点")}] {x.Remarks}"))
            .OrderBy(x => x.Label).ToArray();
    }

    public static async Task SaveAsync(Config config, bool enabled, string? aiId, string? defaultId)
    {
        var candidate = JsonUtils.DeepCopy(config);
        candidate.AiPolicy = new AiPolicyOptions { Enabled = enabled, AiProfileId = aiId ?? "" };
        candidate.IndexId = defaultId ?? config.IndexId;
        if (enabled)
        {
            if (string.IsNullOrWhiteSpace(aiId) || string.IsNullOrWhiteSpace(defaultId))
                throw new InvalidOperationException("请分别选择 AI 策略和默认策略的节点。");
            var node = await AppManager.Instance.GetProfileItem(candidate.IndexId)
                ?? throw new InvalidOperationException("默认策略节点已被删除，请重新选择。");
            var built = await CoreConfigContextBuilder.BuildAll(candidate, node);
            if (!built.Success) throw new InvalidOperationException(string.Join("；", built.CombinedValidatorResult.Errors));
            // Pure config generation validates tags/order without stopping the current core.
            var generated = await CoreConfigHandler.GenerateClientConfig(built.MainResult.Context, null);
            if (!generated.Success) throw new InvalidOperationException(generated.Msg);
        }
        var file = Utils.GetConfigPath("guiNConfig.json");
        if (File.Exists(file)) File.Copy(file, Utils.GetBackupPath($"before-traffic-policies-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"));
        var previousPolicy = config.AiPolicy;
        var previousDefault = config.IndexId;
        try
        {
            config.AiPolicy = candidate.AiPolicy;
            config.IndexId = candidate.IndexId;
            if (await ConfigHandler.SaveConfig(config) != 0) throw new IOException("策略保存失败，已恢复原选择。");
        }
        catch
        {
            config.AiPolicy = previousPolicy;
            config.IndexId = previousDefault;
            throw;
        }
    }
}
