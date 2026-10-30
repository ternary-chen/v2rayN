using ServiceLib.Handler.SysProxy;

void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var direct = new WindowsProxyEndpoint(false, false, "", "", "");
var proxy = new WindowsProxyEndpoint(true, false, "127.0.0.1:18808", "<local>", "");
var pac = new WindowsProxyEndpoint(false, false, "", "", "http://127.0.0.1:18811/proxy.pac");
var off = new WindowsProxySnapshot(1, direct, direct, direct);
var on = new WindowsProxySnapshot(3, proxy, proxy, proxy);
Check(off.Matches(1, null, null) && off.IsDirect, "Consistent clear rejected");
Check(on.Matches(2, proxy.Server, proxy.Bypass) && on.ManualEnabled, "Consistent manual rejected");
Check(new WindowsProxySnapshot(5, pac, pac, pac).Matches(4, pac.Pac, ""), "Consistent PAC rejected");

// The exact regression observed on the user's machine: WinINet on, registry/WinHTTP off.
var split = new WindowsProxySnapshot(3, proxy, direct, direct);
Check(!split.IsConsistent && !split.IsDirect && !split.ManualEnabled, "Contradictory state reported healthy");
Check(split.Display.Contains("不一致") && !split.Matches(2, proxy.Server, proxy.Bypass), "Split state accepted as successful enable");
Check(!(off with { Registry = proxy }).IsConsistent, "Reverse registry mismatch hidden");
Check(!(on with { WinHttp = direct }).IsConsistent, "WinHTTP mismatch hidden");
Check(!(on with { WinHttp = proxy with { Server = "127.0.0.1:7897" } }).IsConsistent, "Different proxy accepted");
Check(!(on with { Registry = proxy with { Bypass = "*" } }).IsConsistent, "Bypass mismatch hidden");
Check(!(on with { WinHttp = proxy with { AutoDetect = true } }).IsConsistent, "Autodetect mismatch hidden");
Check(!on.Matches(1, null, null) && !off.Matches(2, proxy.Server, proxy.Bypass), "Wrong operation accepted");
Check(!on.Matches(2, "127.0.0.1:7897", proxy.Bypass), "Wrong requested endpoint accepted");
Check(!(off with { WinInet = direct with { Server = proxy.Server } }).Matches(1, null, null), "Clear accepted stale server");
Check(!(off with { Registry = pac }).IsConsistent, "Stale PAC hidden");
Check(!(off with { WinHttp = direct with { Bypass = "*" } }).Matches(1, null, null), "Clear left WinHTTP bypass behind");
Check(!(off with { Flags = 5 }).IsConsistent, "PAC flag without URL accepted");
var detect = direct with { AutoDetect = true };
Check(new WindowsProxySnapshot(9, detect, direct, detect).AutoDetect, "Consistent autodetect rejected");
Check(!new WindowsProxySnapshot(9, detect, direct, detect).Matches(1, null, null), "Clear left autodetect enabled");
var privateEndpoint = proxy with { Server = "user:password@example.test:8080", Pac = "http://localhost/pac?token=private" };
var detail = new WindowsProxySnapshot(7, privateEndpoint, privateEndpoint, privateEndpoint).Detail;
Check(!detail.Contains("password") && !detail.Contains("token=private"), "Diagnostics exposed proxy credentials");
var enabledReport = WindowsProxyDiagnostics.Describe(on, ServiceLib.Enums.ESysProxyType.ForcedChange, proxy.Server, proxy.Bypass, 18808, true);
Check(enabledReport.Passed == true && enabledReport.Text.Contains("18808") && enabledReport.Text.Contains("WinHTTP"), "Enabled diagnostics missing evidence");
var stoppedReport = WindowsProxyDiagnostics.Describe(on, ServiceLib.Enums.ESysProxyType.ForcedChange, proxy.Server, proxy.Bypass, 18808, false);
Check(stoppedReport.Passed == false && stoppedReport.Summary.Contains("没有监听"), "Configured but stopped core reported healthy");
var clearReport = WindowsProxyDiagnostics.Describe(off, ServiceLib.Enums.ESysProxyType.ForcedClear, proxy.Server, proxy.Bypass, 18808, true);
Check(clearReport.Passed == true && clearReport.Text.Contains("不会停止"), "Clear incorrectly requires stopping the core");
var splitReport = WindowsProxyDiagnostics.Describe(split, ServiceLib.Enums.ESysProxyType.ForcedChange, proxy.Server, proxy.Bypass, 18808, true);
Check(splitReport.Passed == false && splitReport.Text.Contains("不一致项目"), "Split report missing actionable explanation");
Check(!WindowsProxyDiagnostics.Describe(new(7, privateEndpoint, privateEndpoint, privateEndpoint), null, "", "", 18808, null).Text.Contains("password"), "Report exposes credentials");
Console.WriteLine("PASS consistency, requested-mode verification, split-state regression, diagnostics and redaction");
if (args.Contains("--read-only"))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    var actual = WindowsProxySnapshot.Read();
    Console.WriteLine(actual.Detail);
    Check(actual.IsConsistent, "Live Windows proxy sources disagree; no settings were written");
    if (args.Contains("--expect-clear")) Check(actual.Matches(1, null, null), "Expected cleared system proxy");
    if (args.Contains("--expect-enabled")) Check(actual.Matches(2, "127.0.0.1:18808", actual.WinInet.Bypass), "Expected v2rayN proxy at 18808");
    Console.WriteLine("PASS read-only native WinINet/WinHTTP/registry cross-check; no writes");
}
