using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MiHome.Core;
using MiHome.Core.Models;
using Windows.UI;

namespace MiHome.App.Views;

/// <summary>设备页：按家庭/房间分组的设备卡片，开关快速控制 + 状态轮询。</summary>
public sealed partial class DevicesPage : Page
{
    private readonly Dictionary<string, DeviceCard> _cards = new();
    private CancellationTokenSource? _pollCts;
    private bool _loaded;

    public DevicesPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => { if (!_loaded) { _loaded = true; await ReloadAsync(); } StartPolling(); };
        Unloaded += (_, _) => _pollCts?.Cancel();
    }

    // ---------- 数据加载 ----------

    private async Task ReloadAsync()
    {
        Ring.IsActive = true;
        try
        {
            if (!App.Service.LoginStatus())
            {
                ShowLoginHint();
                return;
            }

            var devices = await App.Service.ListDevicesAsync();
            if (App.Settings.HideNoFuncDevices)
            {
                devices = devices.Where(d => !string.IsNullOrEmpty(d.SpecType)).ToList();
            }

            BuildGroups(devices);
            _ = RefreshPowerStatesAsync(devices.Select(d => d.Did).ToList());
            _ = RefreshMetricsAsync(devices.Select(d => d.Did).ToList());
        }
        catch (Exception exc)
        {
            App.ShowMainToast($"获取设备列表失败：{exc.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            Ring.IsActive = false;
        }
    }

    private void ShowLoginHint()
    {
        GroupsPanel.Children.Clear();
        var hint = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        hint.Children.Add(new TextBlock
        {
            Text = "尚未登录米家账号",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var button = new Button { Content = "去扫码登录" };
        button.Click += (_, _) => App.NavigateMain("login");
        hint.Children.Add(button);
        GroupsPanel.Children.Add(hint);
    }

    private void BuildGroups(List<DeviceInfo> devices)
    {
        _cards.Clear();
        GroupsPanel.Children.Clear();

        var groups = devices
            .GroupBy(d => (d.HomeName, d.RoomName))
            .OrderBy(g => g.Key.HomeName).ThenBy(g => g.Key.RoomName)
            .ToList();
        CountText.Text = $"{devices.Count} 台设备";

        foreach (var group in groups)
        {
            GroupsPanel.Children.Add(new TextBlock
            {
                Text = $"{group.Key.HomeName} · {group.Key.RoomName}",
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.7,
                Margin = new Thickness(0, 8, 0, 4),
            });

            var wrap = new ItemsControl
            {
                ItemsPanel = BuildCardPanel(),
            };
            foreach (var device in group)
            {
                var card = new DeviceCard(device, OpenDetail);
                _cards[device.Did] = card;
                wrap.Items.Add(card.Root);
            }
            GroupsPanel.Children.Add(wrap);
        }
    }

    private static ItemsPanelTemplate BuildCardPanel()
    {
        var xaml = """
            <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <StackPanel Orientation="Vertical" />
            </ItemsPanelTemplate>
            """;
        return (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    }

    // ---------- 开关状态与温湿度轮询 ----------

    private void StartPolling()
    {
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(5000, token);
                    var dids = _cards.Keys.ToList();
                    if (dids.Count == 0)
                    {
                        continue;
                    }
                    await DispatcherRunAsync(async () =>
                    {
                        await RefreshPowerStatesAsync(dids);
                        await RefreshMetricsAsync(dids);
                    });
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch
                {
                    // 轮询失败静默重试
                }
            }
        }, token);
    }

    private async Task DispatcherRunAsync(Func<Task> action)
    {
        var tcs = new TaskCompletionSource();
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        await tcs.Task;
    }

    private async Task RefreshPowerStatesAsync(List<string> dids)
    {
        try
        {
            var states = await App.Service.PowerStatesAsync(dids);
            foreach (var (did, state) in states)
            {
                if (_cards.TryGetValue(did, out var card))
                {
                    card.UpdatePowerState(state);
                }
            }
        }
        catch (Exception exc)
        {
            App.ShowMainToast(exc.Message, InfoBarSeverity.Error);
        }
    }

    private async Task RefreshMetricsAsync(List<string> dids)
    {
        try
        {
            var metrics = await App.Service.ReadMetricsAsync(dids);
            foreach (var (did, text) in metrics)
            {
                if (_cards.TryGetValue(did, out var card))
                {
                    card.UpdateMetrics(text);
                }
            }
        }
        catch
        {
            // 副标题读数失败不影响主流程
        }
    }

    // ---------- 交互 ----------

    private async void OpenDetail(DeviceInfo device)
    {
        try
        {
            var detail = await App.Service.DeviceDetailAsync(device.Did);
            var dialog = new DeviceDetailDialog(detail)
            {
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch (Exception exc)
        {
            App.ShowMainToast(exc.Message, InfoBarSeverity.Error);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void Voice_Click(object sender, RoutedEventArgs e) => await App.ShowVoiceDialogAsync(XamlRoot);
}

/// <summary>设备卡片控件：图标占位、名称、房间、在线状态、开关。</summary>
public sealed class DeviceCard
{
    private readonly DeviceInfo _device;
    private readonly Action<DeviceInfo> _openDetail;
    private readonly ToggleSwitch _toggle;
    private readonly TextBlock _metricsText;
    private readonly TextBlock _statusText;
    private readonly Border _root;
    private bool _suppressToggle;

    /// <summary>卡片根元素（加入 ItemsControl 用）。</summary>
    public Border Root => _root;

    public DeviceCard(DeviceInfo device, Action<DeviceInfo> openDetail)
    {
        _device = device;
        _openDetail = openDetail;

        _root = new Border
        {
            Width = 480,
            MaxWidth = 560,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        };

        _toggle = new ToggleSwitch
        {
            OnContent = "",
            OffContent = "",
            Margin = new Thickness(0, 0, 0, 0),
            IsEnabled = false,
        };
        _toggle.Toggled += OnToggle;

        _metricsText = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.8,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _statusText = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.6,
        };

        var nameText = new TextBlock
        {
            Text = device.Name,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(nameText);
        stack.Children.Add(_metricsText);
        stack.Children.Add(_statusText);
        stack.Children.Add(_toggle);

        // 整卡可点开详情
        _root.PointerPressed += (_, _) => _openDetail(device);
        _root.Child = stack;

        UpdateOnline();
    }

    private async void OnToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle)
        {
            return;
        }
        var newValue = _toggle.IsOn;
        try
        {
            _toggle.IsEnabled = false;
            await App.Service.SetPowerStateAsync(_device.Did, newValue);
            _statusText.Text = newValue ? "已打开" : "已关闭";
        }
        catch (Exception exc)
        {
            // 写失败回滚开关显示
            _suppressToggle = true;
            _toggle.IsOn = !newValue;
            _suppressToggle = false;
            App.ShowMainToast(exc.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _toggle.IsEnabled = true;
        }
    }

    public void UpdatePowerState(bool? state)
    {
        _suppressToggle = true;
        if (state == null)
        {
            _toggle.Visibility = Visibility.Collapsed;
        }
        else
        {
            _toggle.Visibility = Visibility.Visible;
            _toggle.IsEnabled = true;
            _toggle.IsOn = state.Value;
        }
        _suppressToggle = false;
        _statusText.Text = state switch
        {
            true => "已打开",
            false => "已关闭",
            _ => _device.Online ? "" : "离线",
        };
    }

    public void UpdateMetrics(string? text)
    {
        _metricsText.Text = text ?? $"{_device.HomeName} · {_device.RoomName}";
        if (text == null)
        {
            _metricsText.Opacity = 0.5;
        }
    }

    private void UpdateOnline()
    {
        if (!_device.Online)
        {
            _root.Opacity = 0.55;
            _statusText.Text = "离线";
        }
    }
}
