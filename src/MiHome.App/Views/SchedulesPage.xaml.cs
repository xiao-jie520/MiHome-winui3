using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MiHome.Core.Models;
using MiHome.Core.Stores;

namespace MiHome.App.Views;

/// <summary>定时页：设备定时开关规则管理（每天某时刻开/关）。</summary>
public sealed partial class SchedulesPage : Page
{
    public SchedulesPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        ListPanel.Children.Clear();
        var rules = ScheduleStore.Load();
        if (rules.Count == 0)
        {
            ListPanel.Children.Add(new TextBlock
            {
                Text = "还没有定时规则。点击「新建定时」给设备添加每天定时开关。",
                Opacity = 0.7,
            });
            return;
        }

        var devices = new List<DeviceInfo>();
        try
        {
            devices = await App.Service.ListDevicesAsync();
        }
        catch { /* 设备名显示退化 */ }
        var nameOf = devices.ToDictionary(d => d.Did, d => d.Name);

        foreach (var rule in rules)
        {
            ListPanel.Children.Add(BuildCard(rule, nameOf.GetValueOrDefault(rule.Did, rule.Did)));
        }
    }

    private Border BuildCard(ScheduleRule rule, string deviceName)
    {
        var actionText = rule.Action == "on" ? "打开" : "关闭";
        var title = new TextBlock
        {
            Text = $"每天 {rule.Time} {actionText}「{deviceName}」",
            FontSize = 15,
        };
        var subtitle = new TextBlock
        {
            Text = rule.Enabled ? "已启用" : "已停用",
            FontSize = 12,
            Opacity = 0.65,
        };

        var enableToggle = new ToggleSwitch
        {
            OnContent = "",
            OffContent = "",
            IsOn = rule.Enabled,
            Margin = new Thickness(0, 0, 8, 0),
        };
        enableToggle.Toggled += (_, _) =>
        {
            ScheduleStore.SetEnabled(rule.Id, enableToggle.IsOn);
            _ = ReloadAsync();
        };

        var deleteButton = new Button { Content = "删除" };
        deleteButton.Click += async (_, _) =>
        {
            var confirm = new ContentDialog
            {
                Title = "删除定时规则",
                Content = $"确定删除「{deviceName}」在 {rule.Time} 的{actionText}规则吗？",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            {
                ScheduleStore.Remove(rule.Id);
                await ReloadAsync();
            }
        };

        var grid = new Grid { ColumnSpacing = 8, Opacity = rule.Enabled ? 1.0 : 0.6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var textStack = new StackPanel { Spacing = 2 };
        textStack.Children.Add(title);
        textStack.Children.Add(subtitle);
        Grid.SetColumn(textStack, 0);
        Grid.SetColumn(enableToggle, 1);
        Grid.SetColumn(deleteButton, 2);
        grid.Children.Add(textStack);
        grid.Children.Add(enableToggle);
        grid.Children.Add(deleteButton);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(14, 8, 14, 8),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddScheduleDialog
        {
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.Result != null)
        {
            await ReloadAsync();
            App.ShowMainToast("定时规则已创建，从生效时刻开始每天执行", InfoBarSeverity.Success);
        }
    }
}

/// <summary>新建定时规则对话框：设备选择 + 时刻 + 动作。</summary>
public sealed class AddScheduleDialog : ContentDialog
{
    public ScheduleRule? Result { get; private set; }

    private readonly ComboBox _deviceCombo = new()
    {
        Header = "设备",
        MinWidth = 320,
    };
    private readonly TextBox _timeBox = new()
    {
        Header = "时刻（24 小时制，如 07:30）",
        PlaceholderText = "07:30",
    };
    private readonly ComboBox _actionCombo = new()
    {
        Header = "动作",
        MinWidth = 320,
        ItemsSource = new List<string> { "打开", "关闭" },
        SelectedIndex = 0,
    };

    public AddScheduleDialog()
    {
        Title = "新建定时规则";
        PrimaryButtonText = "创建";
        CloseButtonText = "取消";
        DefaultButton = ContentDialogButton.Primary;
        IsPrimaryButtonEnabled = false;

        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(_deviceCombo);
        panel.Children.Add(_timeBox);
        panel.Children.Add(_actionCombo);
        Content = panel;

        _timeBox.TextChanged += (_, _) => IsPrimaryButtonEnabled = Validate();
        _ = LoadDevicesAsync();
        PrimaryButtonClick += OnPrimary;
    }

    private async Task LoadDevicesAsync()
    {
        try
        {
            var devices = await App.Service.ListDevicesAsync();
            var usable = devices.Where(d => !string.IsNullOrEmpty(d.SpecType)).ToList();
            _deviceCombo.ItemsSource = usable.Select(d => $"{d.Name}（{d.HomeName}·{d.RoomName}）").ToList();
            _deviceCombo.Tag = usable;
            if (usable.Count > 0)
            {
                _deviceCombo.SelectedIndex = 0;
            }
            IsPrimaryButtonEnabled = Validate();
        }
        catch (Exception exc)
        {
            _deviceCombo.ItemsSource = new List<string> { $"设备列表加载失败：{exc.Message}" };
        }
    }

    private bool Validate()
    {
        if (ScheduleStore.NormalizeTime(_timeBox.Text) == null)
        {
            return false;
        }
        return _deviceCombo.Tag is List<DeviceInfo> { Count: > 0 } && _deviceCombo.SelectedIndex >= 0;
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var time = ScheduleStore.NormalizeTime(_timeBox.Text);
        var devices = _deviceCombo.Tag as List<DeviceInfo>;
        if (time == null || devices == null || _deviceCombo.SelectedIndex < 0)
        {
            args.Cancel = true;
            return;
        }
        var device = devices[_deviceCombo.SelectedIndex];
        var action = _actionCombo.SelectedIndex == 0 ? "on" : "off";
        Result = ScheduleStore.Add(device.Did, time, action);
        if (Result == null)
        {
            args.Cancel = true;
        }
    }
}
