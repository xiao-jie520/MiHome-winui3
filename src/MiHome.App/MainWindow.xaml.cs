using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MiHome.App.Services;
using MiHome.App.Views;
using MiHome.Core;
using MiHome.Core.Models;
using MiHome.Core.Stores;
using Windows.UI;

namespace MiHome.App;

/// <summary>主窗口：侧边导航 + 页面容器 + 通知栏 + 托盘。</summary>
public sealed partial class MainWindow : Window
{
    private readonly TrayIconService _tray;
    private readonly DispatcherTimer _scheduleTimer;

    /// <summary>托盘服务（登录成功后重建菜单用）。</summary>
    public TrayIconService Tray => _tray;

    public MainWindow()
    {
        InitializeComponent();
        Title = "米家控制台";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        _tray = new TrayIconService(this);
        Closed += OnClosed;

        // 定时开关：每 10 秒判定一次（只认正点那一分钟，不补执行）
        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _scheduleTimer.Tick += async (_, _) => await TickSchedulesAsync();
        _scheduleTimer.Start();

        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item)
        {
            NavigateTo(item.Tag?.ToString());
        }
    }

    public void NavigateTo(string? tag)
    {
        ContentFrame.Content = tag switch
        {
            "devices" => new DevicesPage(),
            "scenes" => new ScenesPage(),
            "schedules" => new SchedulesPage(),
            "settings" => new SettingsPage(),
            "login" => new LoginPage(),
            _ => ContentFrame.Content,
        };
    }

    private void VoiceItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        => _ = ShowVoiceDialogAsync();

    private async Task ShowVoiceDialogAsync()
    {
        var devices = await GetOnlineSpeakersAsync();
        var dialog = new VoiceDialog(devices)
        {
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private async Task<List<DeviceInfo>> GetOnlineSpeakersAsync()
    {
        try
        {
            var devices = await App.Service.ListDevicesAsync();
            return devices.Where(d => d.Online && MiHomeService.IsSpeaker(d)).ToList();
        }
        catch
        {
            return new List<DeviceInfo>();
        }
    }

    /// <summary>顶部通知（Toast 栏）。</summary>
    public void ShowToast(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Toast.Severity = severity;
            Toast.Title = severity switch
            {
                InfoBarSeverity.Error => "出错了",
                InfoBarSeverity.Warning => "注意",
                InfoBarSeverity.Success => "完成",
                _ => "提示",
            };
            Toast.Message = message;
            Toast.IsOpen = true;
        });
    }

    private void Toast_CloseButtonClick(InfoBar sender, object args) => Toast.IsOpen = false;

    /// <summary>定时开关判定与执行。</summary>
    private async Task TickSchedulesAsync()
    {
        var rules = ScheduleStore.Load();
        var engine = AppState.Engine;
        engine.SetRules(rules);
        var (due, missed) = engine.Tick();

        foreach (var rule in missed)
        {
            ShowToast($"定时任务已错过：{rule.Time} 的「{rule.Action}」未执行（应用当时未运行）",
                InfoBarSeverity.Warning);
        }

        foreach (var rule in due)
        {
            try
            {
                await App.Service.SetPowerStateAsync(rule.Did, rule.Action == "on");
                ScheduleStore.MarkFired(rule.Id, DateTime.Now.ToString("yyyy-MM-dd"));
                engine.MarkFired(rule.Id);
                ShowToast($"定时任务已执行：{rule.Time} {rule.Action}", InfoBarSeverity.Success);
            }
            catch (Exception exc)
            {
                ShowToast($"定时任务执行失败：{exc.Message}", InfoBarSeverity.Error);
            }
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (App.Settings.MinimizeToTray)
        {
            // 拦截关闭，最小化到托盘
            args.Handled = true;
            AppWindow.Hide();
            return;
        }
        _tray.Dispose();
        Application.Current.Exit();
    }

    /// <summary>从托盘唤出主窗口。</summary>
    public void ActivateFromTray()
    {
        AppWindow.Show();
        Activate();
    }
}

