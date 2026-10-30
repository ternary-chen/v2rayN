using ServiceLib.Handler.SysProxy;
using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace v2rayN.Desktop.Views;

public partial class SystemProxyDiagnosticsWindow : Window
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
            try
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "保存系统代理检查报告", SuggestedFileName = $"proxy-check-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                    DefaultExtension = "txt", FileTypeChoices = [FilePickerFileTypes.TextPlain], ShowOverwritePrompt = true
                });
                if (file == null) return;
                using (file)
                await using (var stream = await file.OpenWriteAsync())
                {
                    stream.SetLength(0);
                    await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
                    await writer.WriteAsync(ReportBox.Text);
                }
                SaveStatus.Text = "已保存报告";
            }
            catch (Exception ex) { SaveStatus.Text = "保存失败：" + ex.Message; }
        };
        Opened += (_, _) => Refresh(null);
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
