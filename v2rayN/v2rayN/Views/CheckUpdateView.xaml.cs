namespace v2rayN.Views;

public partial class CheckUpdateView
{
    public CheckUpdateView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            ViewModel!.ObserveCustomUpdates().DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.CustomUpdateStatus, v => v.txtCustomStatus.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.CustomUpdateNotes, v => v.txtCustomNotes.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.CustomUpdateReady, v => v.btnInstallCustom.IsEnabled).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.EnableCustomAutoUpdate, v => v.chkCustomAutomatic.IsChecked).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.CheckCustomUpdateCmd, v => v.btnDownloadCustom).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.InstallCustomUpdateCmd, v => v.btnInstallCustom).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.CheckUpdateModels, v => v.lstCheckUpdates.ItemsSource).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.EnableUpdateViaProxy, v => v.togEnableUpdateViaProxy.IsChecked).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.CheckOnlyCmd, v => v.btnCheckOnly).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.CheckUpdateCmd, v => v.btnCheckUpdate).DisposeWith(disposables);
        });
    }
}
