using System.Net;
using System.Net.NetworkInformation;

namespace ServiceLib.Handler.SysProxy;

public sealed record WindowsProxyDiagnosticReport(string Summary, string Text, bool? Passed);

public static class WindowsProxyDiagnostics
{
    [SupportedOSPlatform("windows")]
    public static WindowsProxyDiagnosticReport Capture(ESysProxyType? expected = null)
    {
        var snapshot = WindowsProxySnapshot.Read();
        var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        var (server, bypass) = SysProxyHandler.GetWindowsProxyString(AppManager.Instance.Config, port);
        bool? listening = null;
        try
        {
            listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Any(x => x.Port == port && (x.Address.Equals(IPAddress.Loopback) || x.Address.Equals(IPAddress.Any)));
        }
        catch (Exception ex) { Logging.SaveLog("读取本地代理监听状态", ex); }
        return Describe(snapshot, expected, server, bypass, port, listening);
    }

    public static WindowsProxyDiagnosticReport Describe(WindowsProxySnapshot snapshot, ESysProxyType? expected,
        string server, string bypass, int port, bool? listening)
    {
        var mode = expected switch
        {
            ESysProxyType.ForcedClear => "验证已清除",
            ESysProxyType.ForcedChange => "验证自动配置",
            _ => "查看当前状态"
        };
        var lines = new List<string>
        {
            $"检查时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}",
            $"本次检查：{mode}",
            "只读取本机设置，不修改系统代理、不停止内核、不访问外网。", ""
        };
        void Endpoint(string name, WindowsProxyEndpoint endpoint)
        {
            lines.Add($"{name}：手动代理={(endpoint.Manual ? "开启" : "关闭")}");
            lines.Add($"  地址：{Safe(endpoint.Server)}");
            lines.Add($"  PAC：{Safe(endpoint.Pac)}");
            lines.Add($"  绕过规则：{Safe(endpoint.Bypass)}");
        }
        Endpoint($"WinINet（标志 {snapshot.Flags}，自动检测={(snapshot.WinInet.AutoDetect ? "开启" : "关闭")}）", snapshot.WinInet);
        Endpoint($"WinHTTP 当前用户（自动检测={(snapshot.WinHttp.AutoDetect ? "开启" : "关闭")}）", snapshot.WinHttp);
        Endpoint($"注册表（ProxyEnable={(snapshot.Registry.Manual ? 1 : 0)}）", snapshot.Registry);
        lines.Add($"本地 TCP 端口 {port}：{(listening == true ? "正在监听" : listening == false ? "没有监听" : "无法读取")}");
        lines.Add($"三处配置一致：{(snapshot.IsConsistent ? "是" : "否")}");
        var differences = new List<string>();
        if (snapshot.WinInet.Manual != snapshot.Registry.Manual || snapshot.WinInet.Manual != snapshot.WinHttp.Manual) differences.Add("手动代理开关");
        if (snapshot.WinInet.Server != snapshot.Registry.Server || snapshot.WinInet.Server != snapshot.WinHttp.Server) differences.Add("代理地址");
        if (snapshot.WinInet.Pac != snapshot.Registry.Pac || snapshot.WinInet.Pac != snapshot.WinHttp.Pac) differences.Add("PAC 地址");
        if (snapshot.WinInet.AutoDetect != snapshot.WinHttp.AutoDetect) differences.Add("自动检测");
        if (snapshot.WinInet.Manual && (snapshot.WinInet.Bypass != snapshot.Registry.Bypass || snapshot.WinInet.Bypass != snapshot.WinHttp.Bypass)) differences.Add("绕过规则");
        if (((snapshot.Flags & 4) != 0) != (snapshot.WinInet.Pac.Length > 0)) differences.Add("PAC 标志与地址");
        if (differences.Count > 0) lines.Add("不一致项目：" + string.Join("、", differences));

        bool? passed = snapshot.IsConsistent ? null : false;
        var summary = snapshot.IsConsistent ? "读取完成：三处配置一致" : "代理配置不一致，请重新应用";
        if (expected == ESysProxyType.ForcedClear)
        {
            passed = snapshot.Matches(1, null, null);
            summary = passed == true ? "清除成功：三处均已关闭，无代理地址、PAC 或绕过规则残留" : "清除未完整生效：仍有开启项、残留值或配置不一致";
            lines.Add("期望：WinINet 标志=1；手动代理/PAC/自动检测关闭；三处地址、PAC、绕过规则为空。");
            lines.Add("清除后端口仍监听是正常的：清除系统代理不会停止 v2rayN 内核。");
        }
        else if (expected == ESysProxyType.ForcedChange)
        {
            var configured = snapshot.Matches(2, server, bypass);
            passed = configured && listening == true;
            summary = !configured ? "自动配置未完整生效：实际设置与请求值不一致" : listening == true ? "自动配置成功：三处设置一致，本地端口正在监听" :
                listening == false ? "设置正确，但本地代理端口没有监听" : "设置正确，但端口监听状态无法确认";
            lines.Add($"期望：手动代理开启，地址={Safe(server)}；PAC/自动检测关闭。");
            lines.Add($"期望绕过规则：{Safe(bypass)}");
        }
        lines.Add("");
        lines.Add("结论：" + summary);
        lines.Add("本报告不判断 TUN 接管、应用旧连接、代理节点可用性或 OpenAI 登录结果。");
        return new(summary, string.Join(Environment.NewLine, lines), passed);
    }

    private static string Safe(string value) => value.Length == 0 ? "（空）" :
        value.Contains('@') || value.Contains('?') ? "（含私密参数，已隐藏）" : value;
}
