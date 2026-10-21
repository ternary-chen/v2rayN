using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace AdaptiveRouting;

public sealed record TcpProcessOwner(IPEndPoint Local, IPEndPoint Remote, int Pid, string Name);

/// <summary>A fresh socket snapshot, never a long-lived source-port/PID cache.</summary>
public static class TcpProcessSnapshot
{
    public static async Task<IReadOnlyList<TcpProcessOwner>> CaptureAsync(IReadOnlySet<int> proxyPorts, CancellationToken ct)
    {
        if (proxyPorts.Count == 0) return [];
        if (OperatingSystem.IsWindows()) return CaptureWindows(proxyPorts);
        if (!OperatingSystem.IsMacOS()) return [];
        // Numeric endpoints only; no shell, DNS lookup, sudo, or permission changes.
        using var process = new Process { StartInfo = new ProcessStartInfo("/usr/sbin/lsof")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        }};
        foreach (var arg in new[] { "-nP", "+c", "0", "-a", "-iTCP:" + string.Join(",", proxyPorts.Order()), "-sTCP:ESTABLISHED", "-Fpcn" })
            process.StartInfo.ArgumentList.Add(arg);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(bounded.Token);
            var error = process.StandardError.ReadToEndAsync(bounded.Token);
            await process.WaitForExitAsync(bounded.Token);
            var text = await output;
            await error; // Drain, but don't put platform diagnostics or paths into connection logs.
            return ParseLsof(text);
        }
        finally
        {
            try { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)); } } catch { }
        }
    }

    public static TcpProcessOwner? Find(IReadOnlyList<TcpProcessOwner> rows, IPEndPoint source, int proxyPort)
    {
        // LAN clients cannot be attributed to processes on this computer.
        if (!IPAddress.IsLoopback(source.Address)) return null;
        var matches = rows.Where(x => x.Local.Port == source.Port && SameAddress(x.Local.Address, source.Address)
            && x.Remote.Port == proxyPort && IPAddress.IsLoopback(x.Remote.Address)).Distinct().ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool SameAddress(IPAddress a, IPAddress b) =>
        (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Equals(b.IsIPv4MappedToIPv6 ? b.MapToIPv4() : b);

    public static IReadOnlyList<TcpProcessOwner> ParseLsof(string text)
    {
        var rows = new List<TcpProcessOwner>();
        var pid = 0;
        var name = "";
        foreach (var line in text.Split('\n'))
        {
            if (line.Length < 2) continue;
            var value = line[1..].TrimEnd('\r');
            switch (line[0])
            {
                case 'p': int.TryParse(value, out pid); name = ""; break;
                case 'c': name = value; break;
                case 'n' when pid > 0:
                    var pair = value.Split("->", StringSplitOptions.None);
                    if (pair.Length == 2 && IPEndPoint.TryParse(pair[0], out var local) && IPEndPoint.TryParse(pair[1], out var remote))
                        rows.Add(new(local, remote, pid, name));
                    break;
            }
        }
        return rows;
    }

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, bool order, uint family, uint tableClass, uint reserved);

    private static IReadOnlyList<TcpProcessOwner> CaptureWindows(IReadOnlySet<int> ports)
    {
        var rows = new List<TcpProcessOwner>();
        foreach (var family in new uint[] { 2, 23 })
        {
            uint size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 5, 0); // TCP_TABLE_OWNER_PID_ALL
            for (var attempt = 0; attempt < 3 && size is > 0 and < 32 * 1024 * 1024; attempt++)
            {
                var allocated = checked((int)size);
                var buffer = Marshal.AllocHGlobal(allocated);
                try
                {
                    var result = GetExtendedTcpTable(buffer, ref size, false, family, 5, 0);
                    if (result == 122) continue; // Snapshot grew, retry with the new size.
                    if (result != 0) break;
                    var width = family == 2 ? 24 : 56;
                    var count = Marshal.ReadInt32(buffer);
                    if (count < 0 || count > (allocated - 4) / width) break;
                    for (var i = 0; i < count; i++)
                    {
                        var row = buffer + 4 + i * width;
                        int Word(int offset) => Marshal.ReadInt32(row, offset);
                        int Port(int offset) => (Marshal.ReadByte(row, offset) << 8) | Marshal.ReadByte(row, offset + 1);
                        IPAddress Address(int offset, int length, int scope = 0)
                        {
                            var bytes = new byte[length]; Marshal.Copy(row + offset, bytes, 0, length);
                            return length == 16 ? new IPAddress(bytes, scope) : new IPAddress(bytes);
                        }
                        var remotePort = Port(family == 2 ? 16 : 44);
                        var state = Word(family == 2 ? 0 : 48);
                        if (state != 5 || !ports.Contains(remotePort)) continue; // ESTABLISHED only, never TIME_WAIT's stale PID.
                        var local = family == 2 ? new IPEndPoint(Address(4, 4), Port(8)) : new IPEndPoint(Address(0, 16, Word(16)), Port(20));
                        var remote = family == 2 ? new IPEndPoint(Address(12, 4), remotePort) : new IPEndPoint(Address(24, 16, Word(40)), remotePort);
                        var pid = Word(family == 2 ? 20 : 52);
                        var name = "";
                        try { using var process = Process.GetProcessById(pid); name = process.ProcessName + ".exe"; } catch { }
                        rows.Add(new(local, remote, pid, name));
                    }
                    break;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        return rows;
    }
}
