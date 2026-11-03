using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AdaptiveRouting;

public static class XrayPrivacyPrerequisites
{
    public static void ValidateVersion(string output)
    {
        var match = Regex.Match(output, @"\bXray\s+(\d+\.\d+\.\d+)");
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version) || version < new Version(26, 10, 10))
            throw new InvalidOperationException("此版本的 DNS/TUN 防护需要 Xray 26.10.10 或更新版本；旧服务保持运行，请先更新内核。");
    }

    public static async Task CheckAsync(string executable)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("version");
        using var process = Process.Start(start) ?? throw new IOException("无法读取 Xray 版本，未切换服务。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw new TimeoutException("读取 Xray 版本超时，未切换服务。");
        }
        ValidateVersion(await output + await error);
    }
}
