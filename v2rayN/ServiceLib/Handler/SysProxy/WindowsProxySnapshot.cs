namespace ServiceLib.Handler.SysProxy;

public sealed record WindowsProxyEndpoint(bool Manual, bool AutoDetect, string Server, string Bypass, string Pac);

public sealed record WindowsProxySnapshot(int Flags, WindowsProxyEndpoint WinInet,
    WindowsProxyEndpoint Registry, WindowsProxyEndpoint WinHttp)
{
    // Contradictory observations must never appear healthy.
    public bool IsConsistent => WinInet.Manual == Registry.Manual && WinInet.Manual == WinHttp.Manual
        && WinInet.AutoDetect == WinHttp.AutoDetect
        && Same(WinInet.Server, Registry.Server) && Same(WinInet.Server, WinHttp.Server)
        && Same(WinInet.Pac, Registry.Pac) && Same(WinInet.Pac, WinHttp.Pac)
        && (!WinInet.Manual || (Same(WinInet.Bypass, Registry.Bypass) && Same(WinInet.Bypass, WinHttp.Bypass)))
        && ((Flags & 4) != 0) == !string.IsNullOrEmpty(WinInet.Pac);
    public bool ManualEnabled => IsConsistent && WinInet.Manual;
    public bool PacEnabled => IsConsistent && !string.IsNullOrEmpty(WinInet.Pac);
    public bool AutoDetect => IsConsistent && WinInet.AutoDetect;
    public bool IsDirect => IsConsistent && !ManualEnabled && !PacEnabled && !AutoDetect;
    public string Display => !IsConsistent ? "Windows：代理配置不一致，请重新应用" : IsDirect ? "Windows：系统代理已关闭" :
        "Windows：" + string.Join(" + ", new[] { ManualEnabled ? $"代理已开启：{Safe(WinInet.Server)}" : null, PacEnabled ? "PAC 已开启" : null, AutoDetect ? "自动检测" : null }.Where(x => x != null));
    public string Detail => $"{Display}\nWinINet（标志 {Flags}）：{Describe(WinInet)}\n注册表：{Describe(Registry)}\nWinHTTP 当前用户：{Describe(WinHttp)}\n此状态不代表 TUN 或程序自设代理的状态。";
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);
    private static string Safe(string value) => value.Contains('@') || value.Contains('?') ? "含私密参数（已隐藏）" : value;
    private static string Describe(WindowsProxyEndpoint e) => $"手动代理={(e.Manual ? "开" : "关")}，地址={Safe(e.Server)}，PAC={Safe(e.Pac)}，自动检测={(e.AutoDetect ? "开" : "关")}";

    public bool Matches(int type, string? server, string? bypass)
    {
        if (!IsConsistent) return false;
        return type switch
        {
            1 => Flags == 1 && IsDirect && WinInet.Server.Length == 0 && Registry.Bypass.Length == 0 && WinInet.Bypass.Length == 0 && WinHttp.Bypass.Length == 0,
            2 => Flags == 3 && ManualEnabled && !PacEnabled && Same(WinInet.Server, server ?? "") && Same(WinInet.Bypass, bypass ?? ""),
            4 => Flags == 5 && PacEnabled && !ManualEnabled && WinInet.Server.Length == 0 && Same(WinInet.Pac, server ?? ""),
            _ => false
        };
    }

    [SupportedOSPlatform("windows")]
    public static WindowsProxySnapshot Read()
    {
        var (flags, inet) = ProxySettingWindows.ReadConnectionProxy();
        var http = ProxySettingWindows.ReadWinHttpProxy();
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings")
            ?? throw new IOException("无法读取 Windows 系统代理设置。");
        var registry = new WindowsProxyEndpoint(key.GetValue("ProxyEnable") is int enabled && enabled != 0,
            false, key.GetValue("ProxyServer")?.ToString() ?? "", key.GetValue("ProxyOverride")?.ToString() ?? "", key.GetValue("AutoConfigURL")?.ToString() ?? "");
        return new(flags, inet, registry, http);
    }
}
