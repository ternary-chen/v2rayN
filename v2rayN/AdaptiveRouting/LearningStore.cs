using System.Globalization;
using System.Net;
using System.Text.Json;

namespace AdaptiveRouting;

public sealed record LearnedHost(string Host, int Port, string RouteIdentity,
    DateTimeOffset LearnedAt, DateTimeOffset ExpiresAt, string Reason);

public sealed class LearningStore
{
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
        _entries = _entries.Where(x => x.ExpiresAt > _now()).TakeLast(capacity).ToList();
    }

    public IReadOnlyList<LearnedHost> Snapshot()
    {
        lock (_gate) return _entries.Where(x => x.ExpiresAt > _now()).OrderByDescending(x => x.LearnedAt).ToArray();
    }

    public bool Contains(string host, int port, string identity)
    {
        lock (_gate) return _entries.Any(x => x.Host == host && x.Port == port &&
            x.RouteIdentity == identity && x.ExpiresAt > _now());
    }

    public void Learn(string host, int port, string identity, TimeSpan lifetime, string reason)
    {
        lock (_gate)
        {
            _entries.RemoveAll(x => x.ExpiresAt <= _now() || (x.Host == host && x.Port == port && x.RouteIdentity == identity));
            _entries.Add(new(host, port, identity, _now(), _now().Add(lifetime), reason));
            if (_entries.Count > _capacity) _entries.RemoveRange(0, _entries.Count - _capacity);
            Save();
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
