using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Text.Json;

namespace AdaptiveRouting;

/// <summary>Explicit, reversible per-user Chrome/Edge policies. Never invoked on
/// application startup, core start, or as a side effect of changing TUN mode.</summary>
[SupportedOSPlatform("windows")]
public static class BrowserPrivacySupport
{
    public sealed record Entry(string Key, string Name, bool Existed, string? Text, int? Number, string Applied);

    public static string Apply(string backupFile, bool tun)
    {
        var specs = new[]
        {
            (Key: @"Software\Policies\Google\Chrome", Name: "WebRtcIPHandling", Numeric: false),
            (Key: @"Software\Policies\Microsoft\Edge", Name: "WebRtcLocalhostIpHandling", Numeric: false),
            (Key: @"Software\Policies\Google\Chrome", Name: "NetworkPredictionOptions", Numeric: true),
            (Key: @"Software\Policies\Microsoft\Edge", Name: "NetworkPredictionOptions", Numeric: true)
        };
        if (File.Exists(backupFile))
            throw new InvalidOperationException("已有浏览器防护备份；切换模式前请先恢复原设置，再应用新模式。");
        var applied = tun ? "default_public_interface_only" : "disable_non_proxied_udp";
        var entries = new List<Entry>();
        foreach (var spec in specs)
        {
            using var machine = Registry.LocalMachine.OpenSubKey(spec.Key);
            if (machine?.GetValue(spec.Name) != null)
                throw new InvalidOperationException("检测到设备级浏览器策略，不能覆盖。请先在 chrome://policy 或 edge://policy 核对管理来源。");
            using var key = Registry.CurrentUser.OpenSubKey(spec.Key);
            var old = key?.GetValue(spec.Name);
            if (old != null && old is not (string or int))
                throw new InvalidOperationException("现有浏览器策略类型异常，未修改。");
            entries.Add(new(spec.Key, spec.Name, old != null, old as string, old as int?, spec.Numeric ? "2" : applied));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(backupFile))!);
        using (var file = new FileStream(backupFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(file, entries);
        try
        {
            foreach (var item in entries)
            {
                using var key = Registry.CurrentUser.CreateSubKey(item.Key);
                if (item.Name == "NetworkPredictionOptions") key.SetValue(item.Name, 2, RegistryValueKind.DWord);
                else key.SetValue(item.Name, item.Applied, RegistryValueKind.String);
            }
        }
        catch
        {
            // Retain backup if rollback is interrupted; the user can retry Restore.
            Restore(backupFile, requireAppliedValue: false);
            throw;
        }
        return tun
            ? "已应用 Chrome/Edge TUN 配套策略：限制接口暴露并关闭预解析；保留 UDP。请完全退出浏览器后重开，在 policy 页面确认生效，再复测通话与泄漏。切回系统代理前需恢复并重新应用。"
            : "已应用 Chrome/Edge 系统代理配套策略：禁止未代理的 WebRTC UDP 并关闭预解析。请完全退出浏览器后重开；网页通话需要 TCP 中继。系统代理无法接管其他应用或浏览器绕过代理的所有 DNS。";
    }

    public static string Restore(string backupFile, bool requireAppliedValue = true)
    {
        if (!File.Exists(backupFile)) return "没有浏览器策略备份，未修改设置。";
        var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(backupFile)) ?? throw new IOException("备份无效。");
        foreach (var item in entries)
        {
            if (item.Key is not (@"Software\Policies\Google\Chrome" or @"Software\Policies\Microsoft\Edge") ||
                item.Name is not ("WebRtcIPHandling" or "WebRtcLocalhostIpHandling" or "NetworkPredictionOptions"))
                throw new IOException("备份含不支持的策略，未恢复。");
            using var key = Registry.CurrentUser.OpenSubKey(item.Key);
            if (requireAppliedValue && key?.GetValue(item.Name)?.ToString() != item.Applied)
                throw new InvalidOperationException("策略已被其他程序修改，为避免覆盖，未恢复；请手动核对备份。");
        }
        foreach (var item in entries)
        {
            using var key = Registry.CurrentUser.CreateSubKey(item.Key);
            if (!item.Existed) key.DeleteValue(item.Name, false);
            else if (item.Number is { } number) key.SetValue(item.Name, number, RegistryValueKind.DWord);
            else key.SetValue(item.Name, item.Text!, RegistryValueKind.String);
        }
        File.Delete(backupFile);
        return "已恢复 Chrome/Edge 原策略，请完全退出浏览器后重开。";
    }
}
