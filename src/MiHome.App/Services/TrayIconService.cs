using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MiHome.App.Services;

/// <summary>
/// 系统托盘：左键唤出主界面，右键菜单含设备快捷开关与退出。
/// 基于 H.NotifyIcon.WinUI（内部封装 Shell_NotifyIcon 与消息窗口），
/// 比手写 Win32 互操作稳定：原自研版本会触发堆损坏（0xC0000374）。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly MainWindow _owner;
    private readonly TaskbarIcon _icon;
    private bool _disposed;

    public TrayIconService(MainWindow owner)
    {
        _owner = owner;
        _icon = new TaskbarIcon
        {
            ToolTipText = "米家控制台",
            NoLeftClickDelay = true,
        };

        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath))
            {
                _icon.Icon = new System.Drawing.Icon(iconPath);
            }
        }
        catch (Exception exc)
        {
            App.Trace($"托盘图标加载失败: {exc.Message}");
        }

        _icon.LeftClickCommand = new SimpleCommand(_ => _owner.DispatcherQueue.TryEnqueue(_owner.ActivateFromTray));
        _icon.DoubleClickCommand = new SimpleCommand(_ => _owner.ActivateFromTray());
        _icon.ForceCreate(enablesEfficiencyMode: false);
        App.Trace("托盘图标已创建");
        RebuildMenu();
    }

    /// <summary>按当前设备重建托盘菜单（登录成功与刷新后调用）。</summary>
    public async void RebuildMenu()
    {
        var flyout = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "打开主界面" };
        open.Click += (_, _) => _owner.DispatcherQueue.TryEnqueue(_owner.ActivateFromTray);
        flyout.Items.Add(open);
        flyout.Items.Add(new MenuFlyoutSeparator());

        try
        {
            var devices = await App.Service.ListDevicesAsync();
            var controllable = devices
                .Where(d => d.Online && !string.IsNullOrEmpty(d.SpecType))
                .Take(10)
                .ToList();
            if (controllable.Count > 0)
            {
                foreach (var device in controllable)
                {
                    var d = device;
                    var item = new MenuFlyoutItem { Text = d.Name };
                    item.Click += async (_, _) =>
                    {
                        try
                        {
                            var state = await App.Service.PowerStateAsync(d.Did);
                            if (state == null)
                            {
                                App.ShowMainToast($"「{d.Name}」不支持开关控制", InfoBarSeverity.Warning);
                                return;
                            }
                            await App.Service.SetPowerStateAsync(d.Did, !state.Value);
                            App.ShowMainToast($"「{d.Name}」已{(!state.Value ? "打开" : "关闭")}", InfoBarSeverity.Success);
                        }
                        catch (Exception exc)
                        {
                            App.ShowMainToast(exc.Message, InfoBarSeverity.Error);
                        }
                    };
                    flyout.Items.Add(item);
                }
                flyout.Items.Add(new MenuFlyoutSeparator());
            }
        }
        catch
        {
            // 未登录或网络失败：只保留基础菜单
        }

        var exit = new MenuFlyoutItem { Text = "退出" };
        exit.Click += (_, _) =>
        {
            Dispose();
            Application.Current.Exit();
        };
        flyout.Items.Add(exit);

        _icon.ContextFlyout = flyout;
    }

    public void ShowNotification(string title, string message)
    {
        try
        {
            _icon.ShowNotification(title, message, NotificationIcon.Info);
        }
        catch
        {
            // 通知失败不影响主流程
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _icon.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}

/// <summary>轻量 ICommand 实现（托盘左键/双击回调）。</summary>
internal sealed class SimpleCommand : System.Windows.Input.ICommand
{
    private readonly Action<object?> _execute;
    public SimpleCommand(Action<object?> execute) => _execute = execute;
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute(parameter);
}
