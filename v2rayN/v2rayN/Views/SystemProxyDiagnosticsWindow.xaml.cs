using ServiceLib.Handler.SysProxy;
using System.IO;
using System.Text;
namespace v2rayN.Views;

public partial class SystemProxyDiagnosticsWindow : System.Windows.Window
{
    public SystemProxyDiagnosticsWindow()
    {
        InitializeComponent();
        InspectButton.Click += (_, _) => Refresh(null);
        ClearCheckButton.Click += (_, _) => Refresh(ESysProxyType.ForcedClear);
        ProxyCheckButton.Click += (_, _) => Refresh(ESysProxyType.ForcedChange);
        CloseButton.Click += (_, _) => Close();
        SaveReportButton.Click += async (_, _) =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存系统代理检查报告", Filter = "文本报告 (*.txt)|*.txt",
                FileName = $"proxy-check-{DateTime.Now:yyyyMMdd-HHmmss}.txt", DefaultExt = ".txt"
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                await File.WriteAllTextAsync(dialog.FileName, ReportBox.Text, new UTF8Encoding(true));
                SaveStatus.Text = "已保存：" + dialog.FileName;
            }
            catch (Exception ex) { SaveStatus.Text = "保存失败：" + ex.Message; }
        };
        Loaded += (_, _) => Refresh(null);
    }

    private void Refresh(ESysProxyType? expected)
    {
        SaveStatus.Text = "";
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("此检查用于 Windows 系统代理。");
            var report = WindowsProxyDiagnostics.Capture(expected);
            ResultText.Text = report.Summary;
            ReportBox.Text = report.Text;
        }
        catch (Exception ex)
        {
            ResultText.Text = "读取失败，无法确认系统代理状态";
            ReportBox.Text = $"检查时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\n{ResultText.Text}\n{ex.Message}\n本次未修改系统代理。";
        }
    }
}
