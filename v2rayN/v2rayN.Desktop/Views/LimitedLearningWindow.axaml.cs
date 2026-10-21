using AdaptiveRouting;
using Avalonia.Platform.Storage;
using ServiceLib.Services;
using System.Text.Json;

namespace v2rayN.Desktop.Views;

public partial class LimitedLearningWindow : Window
{
    private readonly Config _config;
    private readonly CancellationTokenSource _closed = new();
    private readonly string _undoFile = Utils.GetConfigPath("codex-proxy-last-change.json");
    private sealed record UndoRecord(string Home, CodexConfigChange Change);

    public LimitedLearningWindow() : this(AppManager.Instance.Config) { }

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
        SystemProxyState.Text = "正在读取系统代理状态…";
        Opened += async (_, _) => await RefreshSystemProxyAsync();
        EnableAiPolicy.IsChecked = config.AiPolicy?.Enabled == true;
        AiCatalogState.Text = AiDomainCatalog.Summary;
        Loaded += async (_, _) => await LoadPoliciesAsync();
        RefreshRecords();
        Closed += (_, _) => _closed.Cancel();
    }

    private bool _policiesLoaded;
    private async Task LoadPoliciesAsync()
    {
        if (_policiesLoaded) return;
        try
        {
            var choices = await TrafficPolicyService.ChoicesAsync();
            AiPolicyNode.ItemsSource = choices;
            DefaultPolicyNode.ItemsSource = choices;
            var aiId = string.IsNullOrEmpty(_config.AiPolicy?.AiProfileId) ? _config.IndexId : _config.AiPolicy.AiProfileId;
            AiPolicyNode.SelectedItem = choices.FirstOrDefault(x => x.Id == aiId);
            DefaultPolicyNode.SelectedItem = choices.FirstOrDefault(x => x.Id == _config.IndexId);
            _policiesLoaded = true;
            SavePoliciesButton.IsEnabled = true;
        }
        catch (Exception ex) { Status.Text = "读取策略节点失败：" + ex.Message; }
    }
    private async void SavePolicies_Click(object sender, RoutedEventArgs e)
    {
        SavePoliciesButton.IsEnabled = false;
        try
        {
            await TrafficPolicyService.SaveAsync(_config, EnableAiPolicy.IsChecked == true,
                (AiPolicyNode.SelectedItem as TrafficPolicyChoice)?.Id,
                (DefaultPolicyNode.SelectedItem as TrafficPolicyChoice)?.Id);
            Close(true);
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { SavePoliciesButton.IsEnabled = true; }
    }

    private async void SaveLearning_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var old = _config.LimitedLearning;
            var updated = JsonSerializer.Deserialize<LearningOptions>(JsonSerializer.Serialize(old ?? new()))!;
            updated.Enabled = EnableLearning.IsChecked == true;
            updated.DirectTimeoutMs = int.Parse(DirectTimeout.Text ?? "");
            updated.ProxyTimeoutMs = int.Parse(ProxyTimeout.Text ?? "");
            updated.LifetimeHours = int.Parse(Lifetime.Text ?? "");
            updated.DirectProcesses = (DirectProcesses.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
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
            Close(true);
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
        try { LimitedLearningRuntime.Forget(host); RefreshRecords(); Status.Text = "已删除所选节点的记录并清零次数，下次访问会重新检测。"; }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private async Task RefreshSystemProxyAsync()
    {
        try { SystemProxyState.Text = await SystemProxyStatus.ReadAsync(_config.Inbound?.FirstOrDefault()?.LocalPort ?? 0, _closed.Token); }
        catch (OperationCanceledException) { SystemProxyState.Text = "读取系统代理已取消或超时。"; }
        catch (Exception ex) { SystemProxyState.Text = "读取系统代理失败：" + ex.Message; }
    }
    private async void RefreshSystemProxy_Click(object? sender, RoutedEventArgs e) => await RefreshSystemProxyAsync();
    private async void BrowseCodex_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 Codex/ChatGPT 应用或 codex 可执行文件", AllowMultiple = false,
                FileTypeFilter = [FilePickerFileTypes.All]
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
            var runtime = CodexProxySupport.ResolveRuntimeSelection(path);
            if (runtime is null) { CodexResult.Text = "未找到唯一的 codex 运行时，请填写实际可执行文件的完整路径。"; return; }
            CodexExecutable.Text = runtime;
        }
        catch (Exception ex) { CodexResult.Text = "选择程序失败：" + ex.Message; }
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
