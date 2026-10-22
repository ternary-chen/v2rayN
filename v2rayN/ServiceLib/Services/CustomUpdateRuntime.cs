using AdaptiveRouting;

namespace ServiceLib.Services;

public sealed record CustomUpdateState(string Status, string Notes, bool Ready, bool Busy);

public static class CustomUpdateRuntime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "v2rayN-learning-updates");
    private static CustomUpdateManifest? _ready;
    private static string? _package;
    private static DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    public static CustomUpdateState State { get; private set; } = new($"当前定制版：{CustomBuild.Current.Version}。启动后自动检查并下载；安装需要确认。", "", false, false);
    public static EventChannel<CustomUpdateState> Changed { get; } = new();
    private static void Set(string status, string notes = "", bool ready = false, bool busy = false)
    {
        State = new(status, notes, ready, busy); Changed.Publish(State);
    }

    public static async Task InitializeAsync()
    {
        if (!await Gate.WaitAsync(0)) return;
        try
        {
            if (!Directory.Exists(Root) || CustomBuild.Runtime is not { } runtime) return;
            var readyFile = Path.Combine(Root, "ready.json");
            if (File.Exists(readyFile))
            {
                var manifest = JsonSerializer.Deserialize<CustomUpdateManifest>(await File.ReadAllTextAsync(readyFile), CustomUpdateManifest.Json);
                if (manifest != null && manifest.Build > CustomBuild.Current.Build)
                {
                    var package = Path.Combine(Root, $"package-{manifest.Build}-{runtime}.zip");
                    if (await CustomUpdateClient.VerifyAsync(package, manifest.Validate(runtime, manifest.Version)))
                    { _ready = manifest; _package = package; Set($"{manifest.Version} 已下载并校验，等待确认安装。", manifest.Notes, true); }
                }
            }
            var last = Directory.EnumerateDirectories(Root, "install-*").Select(x => Path.Combine(x, "install-result.json"))
                .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (last != null && !File.Exists(last + ".notified"))
            {
                var result = JsonNode.Parse(await File.ReadAllTextAsync(last));
                if (result?["status"]?.ToString() is "rolled-back" or "recovery-needed" or "not-installed")
                    NoticeManager.Instance.Enqueue("上次定制更新：" + result["status"] + "；" + result["reason"] + "。备份与记录：" + Path.GetDirectoryName(last));
                await File.WriteAllTextAsync(last + ".notified", "shown");
            }
        }
        catch (Exception ex) { Logging.SaveLog("CustomUpdateCache", ex); }
        finally { Gate.Release(); }
    }

    public static async Task TickAsync(Config config)
    {
        if (config.CustomUpdate.Enabled && DateTimeOffset.UtcNow >= _nextAttempt) await CheckAndDownloadAsync(config);
    }
    public static async Task CheckAndDownloadAsync(Config config)
    {
        if (!await Gate.WaitAsync(0)) return;
        var previous = State;
        try
        {
            if (CustomBuild.Runtime is not { } runtime || Utils.IsPackagedInstall())
            { Set("定制自动更新目前支持 Windows x64 便携版和 macOS ARM64 应用包。"); _nextAttempt = DateTimeOffset.UtcNow.AddHours(6); return; }
            Set("正在检查定制版更新…", busy: true);
            using var handler = new SocketsHttpHandler { UseProxy = config.CheckUpdateItem.UpdateViaProxy, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(15), AllowAutoRedirect = true };
            if (handler.UseProxy) handler.Proxy = new WebProxy($"socks5://127.0.0.1:{AppManager.Instance.GetLocalPort(EInboundProtocol.socks)}");
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("v2rayN-learning-updater/1");
            var client = new CustomUpdateClient(http);
            using var metadataTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var manifest = await client.FindAsync(runtime, CustomBuild.Current.Build, metadataTimeout.Token);
            _nextAttempt = DateTimeOffset.UtcNow.AddHours(6);
            if (manifest == null) { _ready = null; _package = null; Set($"当前定制版 {CustomBuild.Current.Version} 已是最新版本。"); return; }
            Directory.CreateDirectory(Root);
            var package = Path.Combine(Root, $"package-{manifest.Build}-{runtime}.zip");
            using var downloadTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            await client.DownloadAsync(manifest, runtime, package, percent => Set($"正在下载 {manifest.Version}：{percent}%（代理继续运行）", manifest.Notes, busy: true), downloadTimeout.Token);
            CustomUpdateArchive.Validate(package, runtime);
            var firstReady = _ready?.Build != manifest.Build;
            await File.WriteAllTextAsync(Path.Combine(Root, "ready.json.tmp"), JsonSerializer.Serialize(manifest, CustomUpdateManifest.Json));
            File.Move(Path.Combine(Root, "ready.json.tmp"), Path.Combine(Root, "ready.json"), true);
            _ready = manifest; _package = package;
            Set($"{manifest.Version} 已下载并通过校验。点击“安装并重启”后才会替换程序。", manifest.Notes, ready: true);
            AppEvents.HasUpdateNotified.Publish(true);
            if (firstReady) NoticeManager.Instance.Enqueue($"定制版 {manifest.Version} 已下载，请在检查更新页面确认安装。");
        }
        catch (Exception ex)
        {
            _nextAttempt = DateTimeOffset.UtcNow.AddHours(1);
            Logging.SaveLog("CustomUpdate", ex);
            Set("更新检查/下载失败，客户端继续运行；一小时后重试，也可手动重试。" + ex.Message, previous.Notes, _ready != null && _package != null);
        }
        finally { Gate.Release(); }
    }

    // This method is called only by the visible Install-and-restart button, never a timer/download callback.
    public static async Task InstallConfirmedAsync(Config config)
    {
        if (!await Gate.WaitAsync(0)) return;
        try
        {
            var manifest = _ready ?? throw new InvalidOperationException("尚无已下载的更新。");
            var package = _package ?? throw new InvalidOperationException("安装包不存在。");
            var runtime = CustomBuild.Runtime ?? throw new InvalidOperationException("系统架构不支持。");
            if (!await CustomUpdateClient.VerifyAsync(package, manifest.Validate(runtime, manifest.Version))) throw new InvalidDataException("安装包校验失败，请重新下载。");
            Set("正在准备安装和回退文件；准备完成后重启代理…", manifest.Notes, busy: true);
            var executable = Path.GetFullPath(Utils.GetExePath());
            var directory = Path.GetDirectoryName(executable)!;
            var install = OperatingSystem.IsMacOS() ? Path.GetFullPath(Path.Combine(directory, "..", "..")) : directory;
            var helperName = OperatingSystem.IsWindows() ? "CustomUpdater.exe" : "CustomUpdater";
            var helper = Path.Combine(directory, helperName);
            if (!File.Exists(helper)) throw new IOException("当前安装缺少独立更新程序，请先安装完整的定制版启动包。");
            var work = Path.Combine(Root, "install-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var copiedHelper = Path.Combine(work, helperName); CustomUpdateInstaller.CopyFile(helper, copiedHelper);
            using var current = Process.GetCurrentProcess();
            var probePorts = new List<int>();
            var mainPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
            if (await CustomUpdateInstaller.Probe(mainPort)) probePorts.Add(mainPort);
            if (LimitedLearningRuntime.Running)
            {
                probePorts.Add(config.LimitedLearning.ListenerPort); probePorts.Add(config.LimitedLearning.ProxyPort);
            }
            var plan = new CustomInstallPlan(1, manifest, runtime, package, work, install, Path.GetFullPath(Utils.GetConfigPath()), executable,
                CustomUpdateInstaller.Hash(executable), current.Id, current.StartTime.ToUniversalTime().Ticks, Guid.NewGuid().ToString("N"), probePorts.Distinct().ToArray());
            CustomUpdateInstaller.ValidatePlan(plan);
            var planPath = Path.Combine(work, "plan.json");
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan, CustomUpdateManifest.Json));
            var start = new ProcessStartInfo(copiedHelper) { WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add(planPath);
            using var updater = Process.Start(start) ?? throw new IOException("独立更新程序未启动。");
            var deadline = DateTime.UtcNow.AddMinutes(3);
            var prepared = Path.Combine(work, "prepared");
            while (!File.Exists(prepared) && !updater.HasExited && DateTime.UtcNow < deadline) await Task.Delay(250);
            if (!File.Exists(prepared) || await File.ReadAllTextAsync(prepared) != plan.Nonce)
            {
                if (!updater.HasExited) updater.Kill(true);
                var report = Path.Combine(work, "install-result.json");
                var detail = File.Exists(report) ? JsonNode.Parse(await File.ReadAllTextAsync(report))?["reason"]?.ToString() : "准备超时或更新程序已退出";
                throw new IOException("未开始替换，当前代理继续运行：" + detail);
            }
            await AppManager.Instance.AppExitAsync(true);
        }
        catch (Exception ex) { Logging.SaveLog("CustomInstall", ex); Set("安装未完成：" + ex.Message, _ready?.Notes ?? "", _ready != null); }
        finally { Gate.Release(); }
    }
    public static async Task ReportHealthyAsync()
    {
        var file = Environment.GetEnvironmentVariable("V2RAYN_UPDATE_HEALTH_FILE");
        var nonce = Environment.GetEnvironmentVariable("V2RAYN_UPDATE_NONCE");
        if (file == null || nonce == null) return;
        Environment.SetEnvironmentVariable("V2RAYN_UPDATE_HEALTH_FILE", null);
        Environment.SetEnvironmentVariable("V2RAYN_UPDATE_NONCE", null);
        try
        {
            if (!Path.GetFullPath(file).StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !Guid.TryParseExact(nonce, "N", out _)) return;
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new CustomHealth(CustomBuild.Current.Build, nonce, Environment.ProcessId, Utils.GetExePath())));
        }
        catch (Exception ex) { Logging.SaveLog("UpdateHealth", ex); }
    }
}
