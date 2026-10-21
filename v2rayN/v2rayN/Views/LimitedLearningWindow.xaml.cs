using AdaptiveRouting;
using Microsoft.Win32;
using ServiceLib.Services;
using System.Text.Json;

namespace v2rayN.Views;

public partial class LimitedLearningWindow : Window
{
    private readonly Config _config;
    private readonly CancellationTokenSource _closed = new();
    private readonly string _undoFile = Utils.GetConfigPath("codex-proxy-last-change.json");
    private sealed record UndoRecord(string Home, CodexConfigChange Change);

    public LimitedLearningWindow(Config config)
    {
        InitializeComponent();
        _config = config;
        var options = config.LimitedLearning ?? new();
        EnableLearning.IsChecked = options.Enabled;
        DirectTimeout.Text = options.DirectTimeoutMs.ToString();
        ProxyTimeout.Text = options.ProxyTimeoutMs.ToString();
        Lifetime.Text = options.LifetimeHours.ToString();
        DirectProcesses.Text = string.Join(Environment.NewLine, options.DirectProcesses);
        LearningState.Text = LimitedLearningRuntime.Running ? "自动检测正在运行。" : "自动检测当前未运行；保存后将重载配置。";
        CodexHome.Text = CodexProxySupport.DefaultHome;
        CodexExecutable.Text = CodexProxySupport.FindDesktopRuntime() ?? "";
        using var registry = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        var server = registry?.GetValue("ProxyServer")?.ToString() ?? "未设置";
        // Do not display embedded proxy credentials.
        if (server.Contains('@')) server = "含凭据的代理地址（已隐藏）";
        SystemProxyState.Text = $"Windows 系统代理：{(registry?.GetValue("ProxyEnable") is int value && value == 1 ? "已开启" : "未开启")}；地址：{server}；v2rayN 本地端口：{config.Inbound?.FirstOrDefault()?.LocalPort}";
        RefreshRecords();
        Closed += (_, _) => _closed.Cancel();
    }

    private async void SaveLearning_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var old = _config.LimitedLearning;
            var updated = JsonSerializer.Deserialize<LearningOptions>(JsonSerializer.Serialize(old ?? new()))!;
            updated.Enabled = EnableLearning.IsChecked == true;
            updated.DirectTimeoutMs = int.Parse(DirectTimeout.Text);
            updated.ProxyTimeoutMs = int.Parse(ProxyTimeout.Text);
            updated.LifetimeHours = int.Parse(Lifetime.Text);
            updated.DirectProcesses = DirectProcesses.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
            updated.Validate();
            if (updated.Enabled && _config.TunModeItem.EnableTun)
                throw new InvalidOperationException("请先关闭 TUN。自动检测仅支持标准 Xray 配置的非 TUN 模式。");
            var path = Utils.GetConfigPath("guiNConfig.json");
            if (File.Exists(path)) File.Copy(path, Utils.GetBackupPath($"before-limited-learning-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"));
            _config.LimitedLearning = updated;
            if (await ConfigHandler.SaveConfig(_config) != 0)
            {
                _config.LimitedLearning = old;
                throw new IOException("保存失败，已保留原设置。");
            }
            DialogResult = true;
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private void RefreshRecords()
    {
        try { Records.ItemsSource = LimitedLearningRuntime.Records(); }
        catch (Exception ex) { Status.Text = "读取学习记录失败：" + ex.Message; }
    }
    private void RefreshRecords_Click(object sender, RoutedEventArgs e) => RefreshRecords();
    private void ForgetRecord_Click(object sender, RoutedEventArgs e)
    {
        if (Records.SelectedItem is not LearnedHost host) return;
        try { LimitedLearningRuntime.Forget(host); RefreshRecords(); Status.Text = "已删除所选记录，下次访问会重新检测。"; }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void BrowseCodex_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Codex runtime|codex.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) CodexExecutable.Text = dialog.FileName;
    }
    private void EnableCodex_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var change = CodexProxySupport.Apply(CodexHome.Text);
            if (change is null) { CodexResult.Text = "系统代理支持已开启。"; return; }
            File.WriteAllText(_undoFile, JsonSerializer.Serialize(new UndoRecord(CodexHome.Text, change)));
            CodexResult.Text = "已启用 [features] respect_system_proxy = true。\n请完全退出并重启 Codex，使桌面会话使用新设置。\n备份：" + change.BackupPath;
        }
        catch (Exception ex) { CodexResult.Text = ex.Message; }
    }
    private void UndoCodex_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(_undoFile)) throw new IOException("没有本功能保存的恢复记录。");
            var undo = JsonSerializer.Deserialize<UndoRecord>(File.ReadAllText(_undoFile))!;
            CodexProxySupport.Undo(undo.Home, undo.Change);
            CodexResult.Text = "已恢复修改前的配置，请重启 Codex。";
        }
        catch (Exception ex) { CodexResult.Text = ex.Message; }
    }
    private async void DiagnoseCodex_Click(object sender, RoutedEventArgs e)
    {
        DiagnoseButton.IsEnabled = false;
        CodexResult.Text = "正在检测 Codex 连接，最多等待 60 秒…";
        try { CodexResult.Text = await CodexProxySupport.DiagnoseAsync(CodexExecutable.Text, CodexHome.Text, _closed.Token); }
        catch (OperationCanceledException) { CodexResult.Text = "诊断已取消或超时。"; }
        catch (Exception ex) { CodexResult.Text = ex.Message; }
        finally { DiagnoseButton.IsEnabled = true; }
    }
}
