using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AdaptiveRouting;

public static class SystemProxyStatus
{
    public static async Task<string> ReadAsync(int localPort, CancellationToken ct)
    {
        string status;
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            var server = key?.GetValue("ProxyServer")?.ToString() ?? "未设置";
            if (server.Contains('@')) server = "含凭据的地址（已隐藏）";
            status = $"Windows 系统代理：{(key?.GetValue("ProxyEnable") is int enabled && enabled == 1 ? "已开启" : "未开启")}；地址：{server}";
        }
        else if (OperatingSystem.IsMacOS())
        {
            var start = new ProcessStartInfo("/usr/sbin/scutil")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("--proxy");
            using var process = Process.Start(start) ?? throw new IOException("无法读取 macOS 系统代理。");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                var text = await output;
                await error;
                if (process.ExitCode != 0) throw new IOException("scutil 未能读取系统代理，请检查系统网络设置。");
                status = FormatMacProxyStatus(text);
            }
            catch
            {
                try { process.Kill(true); } catch { }
                throw;
            }
        }
        else status = "请在系统网络设置中核对代理；此页暂不读取当前平台的系统代理状态。";
        return $"{status}；v2rayN 本地端口：{localPort}";
    }

    public static string FormatMacProxyStatus(string output)
    {
        if (!output.TrimStart().StartsWith("<dictionary> {", StringComparison.Ordinal))
            throw new IOException("无法识别 macOS 系统代理输出，未判断为已关闭。");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var depth = 0;
        foreach (var line in output.Split('\n'))
        {
            // Scoped/supplemental dictionaries may disagree with the effective root settings.
            if (depth == 1)
            {
                var match = Regex.Match(line, @"^\s*(HTTPEnable|HTTPProxy|HTTPPort|HTTPSEnable|HTTPSProxy|HTTPSPort|SOCKSEnable|SOCKSProxy|SOCKSPort|ProxyAutoConfigEnable|ProxyAutoDiscoveryEnable)\s*:\s*(.*?)\s*$");
                if (match.Success) values[match.Groups[1].Value] = match.Groups[2].Value;
            }
            depth += line.Count(c => c == '{') - line.Count(c => c == '}');
        }
        string Route(string kind)
        {
            if (values.GetValueOrDefault(kind + "Enable") != "1") return kind + "：未开启";
            var host = values.GetValueOrDefault(kind + "Proxy", "");
            var safeHost = host.Length is > 0 and <= 253 && Regex.IsMatch(host, @"^[A-Za-z0-9.:\[\]-]+$")
                ? host : "地址已隐藏";
            var port = int.TryParse(values.GetValueOrDefault(kind + "Port"), out var n) && n is > 0 and <= 65535
                ? n.ToString() : "未知端口";
            return $"{kind}：已开启 {safeHost}:{port}";
        }
        var pac = values.GetValueOrDefault("ProxyAutoConfigEnable") == "1" ? "已开启" : "未开启";
        var wpad = values.GetValueOrDefault("ProxyAutoDiscoveryEnable") == "1" ? "已开启" : "未开启";
        return $"macOS 系统代理：{Route("HTTP")}；{Route("HTTPS")}；{Route("SOCKS")}；PAC：{pac}；自动发现：{wpad}";
    }
}
