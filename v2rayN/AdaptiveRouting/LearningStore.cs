using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdaptiveRouting;

public sealed record LearnedHost(string Host, int Port, string RouteIdentity,
    DateTimeOffset LearnedAt, DateTimeOffset ExpiresAt, string Reason, int ConfirmationCount = 1)
{
    [JsonIgnore] public bool IsPermanent => ConfirmationCount >= LearningStore.PromotionThreshold;
    [JsonIgnore] public string ConfirmationDisplay => $"{ConfirmationCount}/{LearningStore.PromotionThreshold}";
    [JsonIgnore] public string ExpiryDisplay => IsPermanent ? "永久" :
        ExpiresAt <= DateTimeOffset.UtcNow ? "已到期，保留累计次数" : ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public sealed class LearningStore
{
    public const int PromotionThreshold = 5;
    private readonly object _gate = new();
    private readonly string _file;
    private readonly Func<DateTimeOffset> _now;
    private readonly int _capacity;
    private List<LearnedHost> _entries;

    public LearningStore(string file, int capacity = 2000, Func<DateTimeOffset>? now = null)
    {
        _file = file;
        _capacity = capacity;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _entries = File.Exists(file)
            ? JsonSerializer.Deserialize<List<LearnedHost>>(File.ReadAllText(file)) ?? [] : [];
        // Keep expired records: they carry confirmation history across TTLs and restarts.
        _entries = _entries.Select(x => x with { ConfirmationCount = Math.Clamp(x.ConfirmationCount, 1, PromotionThreshold) }).ToList();
    }

    public IReadOnlyList<LearnedHost> Snapshot(bool includeExpired = false)
    {
        lock (_gate) return _entries.Where(x => includeExpired || IsActive(x))
            .OrderByDescending(x => x.LearnedAt).ToArray();
    }

    private bool IsActive(LearnedHost entry) => entry.IsPermanent || entry.ExpiresAt > _now();

    public LearnedHost? FindActive(string host, int port, string identity)
    {
        lock (_gate) return _entries.FirstOrDefault(x => x.Host == host && x.Port == port &&
            x.RouteIdentity == identity && IsActive(x));
    }

    public bool Contains(string host, int port, string identity) => FindActive(host, port, identity) is not null;

    public LearnedHost? Learn(string host, int port, string identity, TimeSpan lifetime, string reason)
    {
        lock (_gate)
        {
            var existing = _entries.FirstOrDefault(x => x.Host == host && x.Port == port && x.RouteIdentity == identity);
            // Repeated connections and overlapping callers cannot increment an active record.
            if (existing is not null && IsActive(existing)) return existing;
            while (existing is null && _entries.Count >= _capacity)
            {
                var oldest = _entries.Where(x => !x.IsPermanent).OrderBy(x => x.LearnedAt).FirstOrDefault();
                if (oldest is null) return null; // Permanent records require explicit deletion.
                _entries.Remove(oldest);
            }
            var count = Math.Min(PromotionThreshold, (existing?.ConfirmationCount ?? 0) + 1);
            var now = _now();
            var learned = new LearnedHost(host, port, identity, now,
                count >= PromotionThreshold ? DateTimeOffset.MaxValue : now + lifetime, reason, count);
            if (existing is not null) _entries.Remove(existing);
            _entries.Add(learned);
            Save();
            return learned;
        }
    }

    public void Forget(string host, int port, string identity)
    {
        lock (_gate)
        {
            _entries.RemoveAll(x => x.Host == host && x.Port == port && x.RouteIdentity == identity);
            Save();
        }
    }

    public void Clear()
    {
        lock (_gate) { _entries.Clear(); Save(); }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _file, true);
    }

    public static string NormalizeHost(string host)
    {
        host = host.TrimEnd('.');
        if (IPAddress.TryParse(host, out var address)) return address.ToString();
        var ascii = new IdnMapping().GetAscii(host).ToLowerInvariant();
        if (ascii.Length > 253 || Uri.CheckHostName(ascii) != UriHostNameType.Dns)
            throw new ArgumentException("Invalid destination hostname");
        return ascii;
    }

    public static bool IsLearningCandidate(string host, int port) =>
        port is 80 or 443 && !IPAddress.TryParse(host, out _) && host.Contains('.') &&
        !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) &&
        !host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) &&
        !host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) &&
        !host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
}
