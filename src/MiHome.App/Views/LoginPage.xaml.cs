using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using MiHome.Core.Api;
using QRCoder;
using Windows.Storage.Streams;

namespace MiHome.App.Views;

/// <summary>扫码登录页：米家 APP 扫二维码完成登录。</summary>
public sealed partial class LoginPage : Page
{
    private CancellationTokenSource? _waitCts;

    public LoginPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await StartLoginFlowAsync();
        Unloaded += (_, _) => _waitCts?.Cancel();
    }

    private async Task StartLoginFlowAsync()
    {
        RefreshButton.Visibility = Visibility.Collapsed;
        LogoutButton.Visibility = Visibility.Collapsed;
        QrBorder.Visibility = Visibility.Collapsed;
        Ring.IsActive = true;

        try
        {
            SetStatus("正在检查登录状态…");
            var loginData = await App.Service.QrLoginBeginAsync();
            if (loginData == null)
            {
                // 本地凭据已自动刷新，无需扫码
                LoginSucceeded();
                return;
            }

            SetStatus("请使用米家 APP 扫描二维码");
            Ring.IsActive = false;
            await RenderQrAsync(loginData["loginUrl"]?.ToString() ?? "");
            QrBorder.Visibility = Visibility.Visible;
            RefreshButton.Visibility = Visibility.Visible;

            // 长轮询等待扫码（可达两分钟）
            _waitCts = new CancellationTokenSource();
            var token = _waitCts.Token;
            SetStatus("等待扫码确认…（约两分钟内有效）");
            await Task.Run(async () =>
            {
                await App.Service.QrLoginWaitAsync(loginData);
            }, token);
            LoginSucceeded();
        }
        catch (OperationCanceledException)
        {
            // 页面已卸载
        }
        catch (Exception exc)
        {
            SetStatus($"登录失败：{exc.Message}");
            Ring.IsActive = false;
            RefreshButton.Visibility = Visibility.Visible;
        }
    }

    private async Task RenderQrAsync(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return;
        }
        using var generator = new QRCodeGenerator();
        var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.L);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule: 6);
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        QrImage.Source = bitmap;
    }

    private void LoginSucceeded()
    {
        SetStatus("登录成功！");
        Ring.IsActive = false;
        LogoutButton.Visibility = Visibility.Visible;
        // 通知托盘重建菜单并切到设备页
        App.ServiceTray?.RebuildMenu();
        App.NotifyLoginSuccess();
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _waitCts?.Cancel();
        await StartLoginFlowAsync();
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        App.Service.Logout();
        _ = StartLoginFlowAsync();
    }
}
