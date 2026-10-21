using AdaptiveRouting;

namespace ServiceLib.Services;

public static class LimitedLearningRuntime
{
    private static LearningProxy? _proxy;
    public static string StoragePath => Utils.GetConfigPath("limited-learning.json");
    public static bool Running => _proxy is not null;

    public static void Start(CoreConfigContext context, Action<string> message)
    {
        var options = context.AppConfig.LimitedLearning ?? new();
        if (!options.Enabled) return;
        if (context.RunCoreType != ECoreType.Xray || context.IsTunEnabled || context.Node.ConfigType == EConfigType.Custom)
            throw new InvalidOperationException("有限学习仅支持标准 Xray 非 TUN 模式。");
        // Hash identity rather than storing node credentials in the learning database.
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonUtils.Serialize(context.Node))));
        var proxy = new LearningProxy(options, StoragePath, identity);
        var logFormatter = CoreConnectionLogs.Formatter(context);
        proxy.ConnectionEvent += entry =>
        {
            var line = logFormatter.FormatLearning(entry);
            Logging.SaveConnectionLog(line);
            message(line);
        };
        var defaultName = ConnectionLogFormatter.Safe(context.Node.Remarks ?? "未命名节点");
        proxy.Message += text =>
        {
            var entry = $"[自动分流诊断] [默认候选节点:{defaultName}] {text}";
            Logging.SaveLog(entry);
            message(entry);
        };
        try { proxy.Start(); _proxy = proxy; }
        catch { proxy.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
    }

    public static async Task StopAsync()
    {
        var proxy = Interlocked.Exchange(ref _proxy, null);
        if (proxy is not null) await proxy.DisposeAsync();
    }

    public static IReadOnlyList<LearnedHost> Records() =>
        _proxy?.Store.Snapshot(includeExpired: true) ?? new LearningStore(StoragePath).Snapshot(includeExpired: true);

    public static void Forget(LearnedHost host)
    {
        if (_proxy is not null) _proxy.Forget(host);
        else
        {
            new LearningStore(StoragePath).Forget(host.Host, host.Port, host.RouteIdentity);
            Logging.SaveLog($"[自动分流] {host.Host}:{host.Port}: 已手动删除所选节点的记录并清零次数。");
        }
    }
}
