using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MiHome.Core;
using MiHome.Core.Api;
using MiHome.Core.Stores;
using MiHome.App.Services;

namespace MiHome.App;

/// <summary>应用入口：初始化服务与设置。</summary>
public partial class App : Application
{
    public static MijiaApiClient Api { get; private set; } = null!;
    public static MiHomeService Service { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = null!;

    public static Window? MainWindow { get; set; }

    public App()
    {
        Trace("App ctor start");
        InitializeComponent();
        Trace("App InitializeComponent done");
        Api = new MijiaApiClient();
        Trace("ApiClient done");
        Service = new MiHomeService(Api);
        Trace("Service done");
        try
        {
            Settings = SettingsStore.Get();
            Trace("Settings done");
        }
        catch (Exception exc)
        {
            Trace($"Settings 异常: {exc}");
            Settings = new AppSettings();
        }

        // 全局异常日志（启动失败排查）
        this.UnhandledException += (_, e) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "MiHome-crash.log"),
                    $"[{DateTime.Now:HH:mm:ss}] {e.Exception}\n{e.Exception.StackTrace}\n----\n");
            }
            catch { /* ignore */ }
        };
    }

    public static void Trace(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "MiHome-boot.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { /* ignore */ }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Trace("OnLaunched");
        try
        {
            MainWindow = new MainWindow();
            Trace("MainWindow created");
            ApplyTheme();
            if (Settings.StartMinimized)
            {
                MainWindow.AppWindow.Hide();
            }
            else
            {
                MainWindow.Activate();
            }
            Trace("Activated");
        }
        catch (Exception exc)
        {
            Trace($"OnLaunched 异常: {exc}");
            throw;
        }
    }

    public static void ApplyTheme()
    {
        if (MainWindow is MainWindow mw && mw.Content is FrameworkElement root)
        {
            root.RequestedTheme = Settings.Theme switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }

    public static void SaveSettings()
    {
        SettingsStore.Save(Settings);
    }

    // ---------- 全局 UI 助手（各页面/对话框调用） ----------

    private static MainWindow Main => MainWindow as MainWindow
        ?? throw new InvalidOperationException("主窗口未初始化");

    /// <summary>托盘服务（登录成功后由 LoginPage 重建菜单）。</summary>
    public static TrayIconService? ServiceTray => Main is { } m ? m.Tray : null;

    /// <summary>登录成功：切回设备页。</summary>
    public static void NotifyLoginSuccess()
    {
        Main.DispatcherQueue.TryEnqueue(() => NavigateMain("devices"));
    }

    /// <summary>主窗口导航。</summary>
    public static void NavigateMain(string tag)
    {
        Main.DispatcherQueue.TryEnqueue(() => Main.NavigateTo(tag));
    }

    /// <summary>主窗口顶部通知栏。</summary>
    public static void ShowMainToast(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        try
        {
            Main.ShowToast(message, severity);
        }
        catch
        {
            // 窗口未就绪时静默
        }
    }

    /// <summary>打开小爱语音对话框（任意页面调用）。</summary>
    public static async Task ShowVoiceDialogAsync(Microsoft.UI.Xaml.XamlRoot xamlRoot)
    {
        try
        {
            var devices = await Service.ListDevicesAsync();
            var speakers = devices.Where(d => d.Online && MiHomeService.IsSpeaker(d)).ToList();
            var dialog = new Views.VoiceDialog(speakers) { XamlRoot = xamlRoot };
            await dialog.ShowAsync();
        }
        catch (Exception exc)
        {
            ShowMainToast(exc.Message, InfoBarSeverity.Error);
        }
    }
}
