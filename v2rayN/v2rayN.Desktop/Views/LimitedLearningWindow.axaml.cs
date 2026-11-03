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
        InitializeManualRules();
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
        EnableCommunityPolicy.IsChecked = config.AiPolicy?.CommunityProxyToDedicated != false;
        AiCatalogState.Text = AiDomainCatalog.Summary;
        Loaded += async (_, _) => await LoadPoliciesAsync();
        RefreshRecords();
        Closed += (_, _) => _closed.Cancel();
    }


    private readonly System.Collections.ObjectModel.ObservableCollection<ManualRouteRule> _manualRules = new();
    private ManualRouteRule? _editingRule;

    private void InitializeManualRules()
    {
        foreach (var rule in _config.ManualRules ?? []) _manualRules.Add(rule.Copy());
        ManualRulesGrid.ItemsSource = _manualRules;
        RuleKind.ItemsSource = ManualRouteRule.Kinds;
        RuleTarget.ItemsSource = ManualRouteRule.Targets;
        ResetRuleEditor();
        UpdateRuleCount();
    }

    private void UpdateRuleCount() => RuleCount.Text = _manualRules.Count == 0
        ? "暂无手动规则 · 在左侧添加，原有分流继续生效"
        : $"手动规则 {_manualRules.Count} 条 · 已启用 {_manualRules.Count(x => x.Enabled)} 条 · 上方优先";

    private void ResetRuleEditor()
    {
        _editingRule = null;
        RuleEditorTitle.Text = "新增规则";
        ApplyRuleButton.Content = "添加到顶部";
        RuleKind.SelectedItem = ManualRouteRule.Kinds[0];
        RuleTarget.SelectedItem = ManualRouteRule.Targets[0];
        RuleValue.Text = RuleRemarks.Text = "";
        RuleEnabled.IsChecked = true;
    }
    private void NewRule_Click(object sender, RoutedEventArgs e) => ResetRuleEditor();
    private void RuleKind_SelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (RuleHint is null) return;
        RuleHint.Text = (RuleKind.SelectedItem as RuleChoice)?.Value switch
        {
            "domain-suffix" => "example.com：包含其所有子域名；无需填 domain:。",
            "domain" => "api.example.com：只匹配这个完整域名。",
            "keyword" => "example：匹配域名中包含的文字。",
            "ip" => "例如 192.168.1.1、10.0.0.0/8 或 2001:db8::/32。域名连接的 IP 匹配受原路由 DNS 策略影响。",
            "geosite" => "例如 openai、youtube；分类须存在于本机 geosite.dat。",
            "geoip" => "例如 cn、private；分类须存在于本机 geoip.dat。",
            "process" => "例如 chrome 或 chrome.exe；仅匹配内核能识别的进程。",
            "process-dir" => "例如 D:/apps/example/，包含该目录内的程序。",
            _ => ""
        };
    }
    private void ApplyRule_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var rule = new ManualRouteRule
            {
                Kind = (RuleKind.SelectedItem as RuleChoice)?.Value ?? "",
                Target = (RuleTarget.SelectedItem as RuleChoice)?.Value ?? "",
                Value = RuleValue.Text ?? "", Remarks = RuleRemarks.Text ?? "", Enabled = RuleEnabled.IsChecked == true
            }.Normalize();
            if (_editingRule is { } editing && _manualRules.Contains(editing)) _manualRules[_manualRules.IndexOf(editing)] = rule;
            else _manualRules.Insert(0, rule);
            ManualRulesGrid.SelectedItem = rule;
            ResetRuleEditor();
            UpdateRuleCount();
            Status.Text = "规则已放入待保存列表；点击“保存规则（不中断连接）”后生效。";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (ManualRulesGrid.SelectedItem is not ManualRouteRule rule) { Status.Text = "请先选择一条规则。"; return; }
        _editingRule = rule;
        RuleEditorTitle.Text = "编辑选中规则";
        ApplyRuleButton.Content = "更新这条规则";
        RuleKind.SelectedItem = ManualRouteRule.Kinds.FirstOrDefault(x => x.Value == rule.Kind);
        RuleTarget.SelectedItem = ManualRouteRule.Targets.FirstOrDefault(x => x.Value == rule.Target);
        RuleValue.Text = rule.Value;
        RuleRemarks.Text = rule.Remarks;
        RuleEnabled.IsChecked = rule.Enabled;
    }
    private void ToggleRule_Click(object sender, RoutedEventArgs e)
    {
        if (ManualRulesGrid.SelectedItem is not ManualRouteRule rule) return;
        var updated = rule.Copy(); updated.Enabled = !updated.Enabled;
        _manualRules[_manualRules.IndexOf(rule)] = updated;
        ManualRulesGrid.SelectedItem = updated;
        if (ReferenceEquals(_editingRule, rule)) ResetRuleEditor();
        UpdateRuleCount();
    }
    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (ManualRulesGrid.SelectedItem is not ManualRouteRule rule) return;
        _manualRules.Remove(rule);
        if (ReferenceEquals(_editingRule, rule)) ResetRuleEditor();
        UpdateRuleCount();
    }
    private void MoveRule(int offset)
    {
        if (ManualRulesGrid.SelectedItem is not ManualRouteRule rule) return;
        var index = _manualRules.IndexOf(rule);
        if (index + offset < 0 || index + offset >= _manualRules.Count) return;
        _manualRules.Move(index, index + offset);
        ManualRulesGrid.SelectedItem = rule;
    }
    private void MoveRuleUp_Click(object sender, RoutedEventArgs e) => MoveRule(-1);
    private void MoveRuleDown_Click(object sender, RoutedEventArgs e) => MoveRule(1);

    private void ApplyBrowserPrivacy_Click(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows()) { BrowserPrivacyResult.Text = "此入口仅适用于 Windows Chrome/Edge。"; return; }
        try { BrowserPrivacyResult.Text = BrowserPrivacySupport.Apply(Utils.GetConfigPath("browser-privacy-backup.json"), _config.TunModeItem.EnableTun); }
        catch (Exception ex) { BrowserPrivacyResult.Text = ex.Message; }
    }

    private void RestoreBrowserPrivacy_Click(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows()) { BrowserPrivacyResult.Text = "此入口仅适用于 Windows Chrome/Edge。"; return; }
        try { BrowserPrivacyResult.Text = BrowserPrivacySupport.Restore(Utils.GetConfigPath("browser-privacy-backup.json")); }
        catch (Exception ex) { BrowserPrivacyResult.Text = ex.Message; }
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
            var aiId = _config.AiPolicy?.AiProfileId;
            AiPolicyNode.SelectedItem = choices.FirstOrDefault(x => x.Id == aiId);
            DefaultPolicyNode.SelectedItem = choices.FirstOrDefault(x => x.Id == _config.IndexId);
            _policiesLoaded = true;
            SavePoliciesButton.IsEnabled = true;
            SaveNodesButton.IsEnabled = true;
        }
        catch (Exception ex) { Status.Text = "读取策略节点失败：" + ex.Message; }
    }
    private async void SavePolicies_Click(object sender, RoutedEventArgs e) => await SavePoliciesAsync(false);
    private async void SaveNodes_Click(object sender, RoutedEventArgs e) => await SavePoliciesAsync(true);
    private async Task SavePoliciesAsync(bool allowNodeRestart)
    {
        if (_editingRule != null || !string.IsNullOrWhiteSpace(RuleValue.Text))
        {
            Status.Text = "左侧还有未加入列表的规则，请先点击添加 / 更新，或清空表单。";
            return;
        }
        SavePoliciesButton.IsEnabled = false;
        SaveNodesButton.IsEnabled = false;
        try
        {
            var requiresReload = await TrafficPolicyService.SaveAndApplyAsync(_config, EnableAiPolicy.IsChecked == true,
                (AiPolicyNode.SelectedItem as TrafficPolicyChoice)?.Id,
                (DefaultPolicyNode.SelectedItem as TrafficPolicyChoice)?.Id, _manualRules,
                (DirectProcesses.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries), allowNodeRestart, EnableCommunityPolicy.IsChecked == true);
            if (requiresReload) Close(true);
            else Status.Text = "规则已热更新：内核未重启，已有连接保持原出口，新连接按新规则匹配。";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { SavePoliciesButton.IsEnabled = true; SaveNodesButton.IsEnabled = true; }
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
