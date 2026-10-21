using System.Collections.Concurrent;
using System.Diagnostics;

namespace AdaptiveRouting;

public enum ConnectionRoute { Direct, Proxy }
public sealed record ProbeResult(bool Success, string Reason);

/// <summary>Only called after community rules have failed to classify a destination.</summary>
public sealed class LearningDecision(LearningStore store, LearningOptions options, string identity,
    Func<string, int, ConnectionRoute, CancellationToken, Task<ProbeResult>> probe, Func<DateTimeOffset>? now = null)
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly SemaphoreSlim[] _hostGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, ConnectionRoute? Route)> _recent = new();
    public event Action<string>? DecisionMade;

    public async Task<ConnectionRoute> DecideAsync(string host, int port, CancellationToken ct)
    {
        var key = host + ":" + port;
        void Log(string message) => DecisionMade?.Invoke($"{key}: {message}");
        if (!LearningStore.IsLearningCandidate(host, port))
        {
            Log("本地地址、IP 或非网页目标，直接连接，不参与学习。");
            return ConnectionRoute.Direct;
        }
        var gate = _hostGates[(int)(unchecked((uint)key.GetHashCode()) % (uint)_hostGates.Length)];
        await gate.WaitAsync(ct);
        try
        {
            if (store.FindActive(host, port, identity) is { } learned)
            {
                Log($"命中{(learned.IsPermanent ? "永久" : "临时")}代理记录，累计确认 {learned.ConfirmationDisplay}，本次不增加次数。");
                return ConnectionRoute.Proxy;
            }
            if (_recent.TryGetValue(key, out var recent) && recent.Until > _now())
            {
                if (recent.Route is null)
                {
                    Log("命中两路失败冷却期，未重复探测，未增加次数。");
                    throw new IOException("Both routes recently failed; retry after cooldown");
                }
                Log($"命中{(recent.Route == ConnectionRoute.Direct ? "直连" : "代理")}缓存，未重复探测，未增加次数。");
                return recent.Route.Value;
            }
            Log("没有有效路由缓存，开始直连检测。");
            var watch = Stopwatch.StartNew();
            var direct = await probe(host, port, ConnectionRoute.Direct, ct);
            ct.ThrowIfCancellationRequested();
            Log($"直连检测{(direct.Success ? "成功" : "失败")}，{watch.ElapsedMilliseconds} ms，{direct.Reason}。");
            if (direct.Success)
            {
                Remember(key, ConnectionRoute.Direct, TimeSpan.FromMinutes(5));
                Log("选择直连，缓存 5 分钟，未增加代理确认次数。");
                return ConnectionRoute.Direct;
            }
            Log("开始代理检测。");
            watch.Restart();
            var proxied = await probe(host, port, ConnectionRoute.Proxy, ct);
            ct.ThrowIfCancellationRequested();
            Log($"代理检测{(proxied.Success ? "成功" : "失败")}，{watch.ElapsedMilliseconds} ms，{proxied.Reason}。");
            if (!proxied.Success)
            {
                Remember(key, null, TimeSpan.FromSeconds(20));
                Log("直连与代理检测均失败，冷却 20 秒，未新增代理记录，未增加次数。");
                throw new IOException("Direct and proxy probes failed");
            }
            // Probes use separate connections, before any original application bytes are sent.
            var record = store.Learn(host, port, identity, TimeSpan.FromHours(options.LifetimeHours),
                $"Direct: {direct.Reason}; proxy: {proxied.Reason}");
            if (record is null)
            {
                Remember(key, ConnectionRoute.Proxy, TimeSpan.FromMinutes(5));
                Log("名单容量已满且均为永久记录，本次选择代理并缓存 5 分钟，未保存新记录。");
            }
            else
                Log($"直连失败、代理成功，累计确认 {record.ConfirmationDisplay}；" +
                    (record.IsPermanent ? "已转为永久代理记录。" : $"临时代理 {options.LifetimeHours} 小时。"));
            return ConnectionRoute.Proxy;
        }
        catch (OperationCanceledException) { Log("检测已取消，未增加确认次数。"); throw; }
        finally { gate.Release(); }
    }

    public void Forget(string host, int port, string? routeIdentity = null)
    {
        store.Forget(host, port, routeIdentity ?? identity);
        if (routeIdentity is null || routeIdentity == identity)
            _recent.TryRemove(host + ":" + port, out _);
        DecisionMade?.Invoke($"{host}:{port}: 已手动删除所选节点的学习记录并清零次数，下次重新检测。");
    }

    private void Remember(string key, ConnectionRoute? route, TimeSpan ttl)
    {
        if (_recent.Count >= options.MaxEntries) _recent.Clear();
        _recent[key] = (_now().Add(ttl), route);
    }
}
