using DialogHostAvalonia;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class StatusBarView : ReactiveUserControl<StatusBarViewModel>
{
    private static Config _config;

    public StatusBarView()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;

        btnProxyDiagnostics.IsVisible = OperatingSystem.IsWindows();
        btnProxyDiagnostics.Click += async (_, _) =>
        {
            if (Avalonia.Controls.TopLevel.GetTopLevel(this) is Avalonia.Controls.Window owner)
                await new SystemProxyDiagnosticsWindow().ShowDialog(owner);
        };


        txtRunningServerDisplay.Tapped += TxtRunningServerDisplay_Tapped;
        txtRunningInfoDisplay.Tapped += TxtRunningServerDisplay_Tapped;

        void RegisterRepeatAction(Avalonia.Controls.ComboBoxItem item, ESysProxyType type)
        {
            var repeated = false;
            item.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, (_, e) =>
                repeated = e.GetCurrentPoint(item).Properties.IsLeftButtonPressed && cmbSystemProxy.SelectedIndex == (int)type,
                Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
            item.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, async (_, _) =>
            {
                var apply = repeated;
                repeated = false;
                if (!apply || ViewModel == null) return;
                cmbSystemProxy.IsDropDownOpen = false;
                await ViewModel.ApplySystemProxyAsync(type);
            }, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        }
        cmbSystemProxy.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, async (_, e) =>
        {
            if (!cmbSystemProxy.IsDropDownOpen || e.Key != Avalonia.Input.Key.Enter || ViewModel == null) return;
            e.Handled = true;
            cmbSystemProxy.IsDropDownOpen = false;
            await ViewModel.ApplySystemProxyAsync((ESysProxyType)cmbSystemProxy.SelectedIndex);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        RegisterRepeatAction(clearProxyAction, ESysProxyType.ForcedClear);
        RegisterRepeatAction(setProxyAction, ESysProxyType.ForcedChange);
        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.ActualSystemProxyStatus, v => v.txtActualSystemProxy.Text).DisposeWith(disposables);
            this.WhenAnyValue(x => x.ViewModel.ActualSystemProxyDetail).Subscribe(detail => Avalonia.Controls.ToolTip.SetTip(txtActualSystemProxy, detail)).DisposeWith(disposables);
            //status bar
            this.OneWayBind(ViewModel, vm => vm.InboundDisplay, v => v.txtInboundDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.InboundLanDisplay, v => v.txtInboundLanDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.RunningServerDisplay, v => v.txtRunningServerDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.RunningInfoDisplay, v => v.txtRunningInfoDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.SpeedProxyDisplay, v => v.txtSpeedProxyDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.SpeedDirectDisplay, v => v.txtSpeedDirectDisplay.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.EnableTun, v => v.togEnableTun.IsChecked).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.SystemProxySelected, v => v.cmbSystemProxy.SelectedIndex).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedRouting, v => v.cmbRoutings2.SelectedItem).DisposeWith(disposables);

            ViewModel.SetClipboardDataInteraction.RegisterHandler(async interaction =>
            {
                var strData = interaction.Input;
                await AvaUtils.SetClipboardData(this, strData);
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);

            ViewModel.PasswordInputInteraction.RegisterHandler(async interaction =>
            {
                var result = await PasswordInputAsync();
                interaction.SetOutput(result);
            }).DisposeWith(disposables);

            ViewModel.DispatcherRefreshIconInteraction.RegisterHandler(interaction =>
            {
                Dispatcher.UIThread.Post(RefreshIcon, DispatcherPriority.Default);
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);
        });

        //spEnableTun.IsVisible = (Utils.IsWindows() || AppHandler.Instance.IsAdministrator);

        if (Utils.IsNonWindows() && cmbSystemProxy.Items.IsReadOnly == false)
        {
            cmbSystemProxy.Items.RemoveAt(cmbSystemProxy.Items.Count - 1);
        }

        // Because this view has not yet been initialized when DispatcherRefreshIconInteraction is first called.
        RefreshIcon();
    }

    private void RefreshIcon()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow.Icon = AvaUtils.GetAppIcon(_config.SystemProxyItem.SysProxyType);
            var iconslist = TrayIcon.GetIcons(Application.Current);
            iconslist[0].Icon = desktop.MainWindow.Icon;
            TrayIcon.SetIcons(Application.Current, iconslist);
        }
    }

    private async Task<string?> PasswordInputAsync()
    {
        var dialog = new SudoPasswordInputView();
        var obj = await DialogHost.Show(dialog);

        var password = obj?.ToString();
        if (password.IsNullOrEmpty())
        {
            togEnableTun.IsChecked = false;
            return password;
        }

        AppManager.Instance.LinuxSudoPwd = password;
        return password;
    }

    private void TxtRunningServerDisplay_Tapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        ViewModel?.TestServerAvailability();
    }
}
