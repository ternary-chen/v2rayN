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
        proxy.Message += message;
        try { proxy.Start(); _proxy = proxy; }
        catch { proxy.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
    }

    public static async Task StopAsync()
    {
        var proxy = Interlocked.Exchange(ref _proxy, null);
        if (proxy is not null) await proxy.DisposeAsync();
    }

    public static IReadOnlyList<LearnedHost> Records() =>
        _proxy?.Store.Snapshot() ?? new LearningStore(StoragePath).Snapshot();

    public static void Forget(LearnedHost host)
    {
        if (_proxy is not null) _proxy.Forget(host);
        else new LearningStore(StoragePath).Forget(host.Host, host.Port, host.RouteIdentity);
    }
}
