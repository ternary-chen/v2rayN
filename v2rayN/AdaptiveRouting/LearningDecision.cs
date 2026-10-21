using System.Collections.Concurrent;

namespace AdaptiveRouting;

public enum ConnectionRoute { Direct, Proxy }
public sealed record ProbeResult(bool Success, string Reason);

/// <summary>Only called after community rules have failed to classify a destination.</summary>
public sealed class LearningDecision(LearningStore store, LearningOptions options, string identity,
    Func<string, int, ConnectionRoute, CancellationToken, Task<ProbeResult>> probe)
{
    private readonly SemaphoreSlim[] _hostGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, ConnectionRoute? Route)> _recent = new();
    public event Action<string>? DecisionMade;

    public async Task<ConnectionRoute> DecideAsync(string host, int port, CancellationToken ct)
    {
        if (!LearningStore.IsLearningCandidate(host, port)) return ConnectionRoute.Direct;
        var key = host + ":" + port;
        var gate = _hostGates[(int)(unchecked((uint)key.GetHashCode()) % (uint)_hostGates.Length)];
        await gate.WaitAsync(ct);
        try
        {
            if (store.Contains(host, port, identity)) return ConnectionRoute.Proxy;
            if (_recent.TryGetValue(key, out var recent) && recent.Until > DateTimeOffset.UtcNow)
                return recent.Route ?? throw new IOException("Both routes recently failed; retry after cooldown");
            var direct = await probe(host, port, ConnectionRoute.Direct, ct);
            if (direct.Success)
            {
                Remember(key, ConnectionRoute.Direct, TimeSpan.FromMinutes(5));
                return ConnectionRoute.Direct;
            }
            ct.ThrowIfCancellationRequested();
            var proxied = await probe(host, port, ConnectionRoute.Proxy, ct);
            if (!proxied.Success)
            {
                Remember(key, null, TimeSpan.FromSeconds(20));
                DecisionMade?.Invoke($"{host}:{port}: 直连与代理检测均失败，未加入名单。");
                throw new IOException("Direct and proxy probes failed");
            }
            // No original application bytes have been sent; probes are separate connections.
            store.Learn(host, port, identity, TimeSpan.FromHours(options.LifetimeHours),
                $"Direct: {direct.Reason}; proxy: {proxied.Reason}");
            DecisionMade?.Invoke($"{host}:{port}: 直连检测失败、代理检测成功，临时代理 {options.LifetimeHours} 小时。");
            return ConnectionRoute.Proxy;
        }
        finally { gate.Release(); }
    }

    public void Forget(string host, int port, string? routeIdentity = null)
    {
        store.Forget(host, port, routeIdentity ?? identity);
        if (routeIdentity is null || routeIdentity == identity)
            _recent.TryRemove(host + ":" + port, out _);
    }

    private void Remember(string key, ConnectionRoute? route, TimeSpan ttl)
    {
        if (_recent.Count >= options.MaxEntries) _recent.Clear();
        _recent[key] = (DateTimeOffset.UtcNow.Add(ttl), route);
    }
}
