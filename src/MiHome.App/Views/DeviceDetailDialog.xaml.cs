using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MiHome.Core.Models;

namespace MiHome.App.Views;

/// <summary>设备详情对话框：按 spec 元数据自动生成控件（开关/滑块/下拉框/按钮）。</summary>
public sealed partial class DeviceDetailDialog : ContentDialog
{
    private readonly DeviceDetail _detail;

    public DeviceDetailDialog(DeviceDetail detail)
    {
        _detail = detail;
        Title = $"{detail.Name}（{detail.Model}）";
        PrimaryButtonText = "关闭";
        DefaultButton = ContentDialogButton.Primary;
        BuildContent();
    }

    private void BuildContent()
    {
        var root = new StackPanel { Spacing = 16, MinWidth = 480 };

        // 属性区
        if (_detail.Props.Count > 0)
        {
            root.Children.Add(new TextBlock { Text = "属性", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var propsPanel = new StackPanel { Spacing = 12 };
            foreach (var prop in _detail.Props.Take(30))
            {
                propsPanel.Children.Add(BuildPropControl(prop));
            }
            var scroll = new ScrollViewer
            {
                Content = propsPanel,
                MaxHeight = 380,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            root.Children.Add(scroll);
        }

        // 动作区
        var executable = _detail.Actions.Where(a => a.Name is not ("execute-text-directive" or "play-text")).Take(12).ToList();
        if (executable.Count > 0)
        {
            root.Children.Add(new TextBlock { Text = "动作（执行前需确认）", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var actionsPanel = new StackPanel { Spacing = 8 };
            foreach (var action in executable)
            {
                actionsPanel.Children.Add(BuildActionButton(action));
            }
            root.Children.Add(actionsPanel);
        }

        if (_detail.Actions.Any(a => a.Name is "execute-text-directive" or "play-text"))
        {
            var voiceBox = new TextBox { Header = "文本指令（发送到该音箱）", PlaceholderText = "例如：把客厅灯打开" };
            var send = new Button { Content = "发送" };
            send.Click += async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(voiceBox.Text))
                {
                    return;
                }
                var action = _detail.Actions.First(a => a.Name is "execute-text-directive" or "play-text");
                await SafeAsync($"指令已发送", () => App.Service.RunActionAsync(_detail.Did, action, voiceBox.Text.Trim()));
            };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = "小爱语音", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(voiceBox);
            panel.Children.Add(send);
            root.Children.Add(panel);
        }

        Content = root;
    }

    private FrameworkElement BuildPropControl(PropInfo prop)
    {
        var header = new TextBlock
        {
            Text = prop.Desc,
            FontSize = 13,
            Opacity = 0.75,
            Margin = new Thickness(0, 0, 0, 2),
        };

        // 可写布尔 → 开关
        if (prop.Type == "bool" && prop.Writable)
        {
            var toggle = new ToggleSwitch
            {
                OnContent = "",
                OffContent = "",
                Header = header,
            };
            _ = LoadToggleAsync(toggle, prop);
            return toggle;
        }

        // 可写数值（带范围）→ 滑块
        if (prop.Writable && prop.Range is { Length: >= 2 } range &&
            prop.Type is "int" or "uint" or "float")
        {
            double min = range[0], max = range[1];
            double step = range.Length >= 3 && range[2] > 0 ? range[2] : 1;
            var slider = new Slider
            {
                Header = header,
                Minimum = min,
                Maximum = max,
                StepFrequency = step,
                Value = min,
            };
            var valueText = new TextBlock { FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, -6, 0, 0) };
            slider.ValueChanged += (_, _) => valueText.Text = $"{slider.Value:0.###}";
            _ = LoadSliderAsync(slider, prop);
            var box = new StackPanel { Spacing = 2 };
            box.Children.Add(slider);
            box.Children.Add(valueText);
            var commit = new Button { Content = "应用" };
            commit.Click += async (_, _) =>
            {
                double v = slider.Value;
                object value = prop.Type == "float" ? v : (object)(long)Math.Round(v);
                await SafeAsync("已设置", () => App.Service.WritePropAsync(_detail.Did, prop, value));
            };
            var wrap = new StackPanel { Spacing = 4 };
            wrap.Children.Add(box);
            wrap.Children.Add(commit);
            return wrap;
        }

        // 可写枚举 → 下拉框
        if (prop.Writable && prop.ValueList is { Count: > 0 } list)
        {
            var combo = new ComboBox
            {
                Header = header,
                ItemsSource = list.Select(v => v.DescZhCn ?? v.Description).ToList(),
                SelectedIndex = 0,
                MinWidth = 180,
            };
            var commit = new Button { Content = "应用" };
            commit.Click += async (_, _) =>
            {
                int idx = combo.SelectedIndex;
                if (idx >= 0 && idx < list.Count)
                {
                    await SafeAsync("已设置", () => App.Service.WritePropAsync(_detail.Did, prop, list[idx].Value));
                }
            };
            var wrap = new StackPanel { Spacing = 4 };
            wrap.Children.Add(combo);
            wrap.Children.Add(commit);
            _ = LoadComboIndexAsync(combo, list, prop);
            return wrap;
        }

        // 其余：只读值展示
        var readOnlyText = new TextBlock { FontSize = 14, Text = "…" };
        _ = LoadReadOnlyAsync(readOnlyText, prop);
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(header);
        panel.Children.Add(readOnlyText);
        return panel;
    }

    private async Task LoadToggleAsync(ToggleSwitch toggle, PropInfo prop)
    {
        try
        {
            var values = await App.Service.ReadPropsAsync(_detail.Did, new List<PropInfo> { prop });
            if (values.TryGetValue(prop.Name, out var v) && v is bool b)
            {
                toggle.IsOn = b;
            }
        }
        catch { /* 读失败保持默认 */ }
    }

    private async Task LoadSliderAsync(Slider slider, PropInfo prop)
    {
        try
        {
            var values = await App.Service.ReadPropsAsync(_detail.Did, new List<PropInfo> { prop });
            if (values.TryGetValue(prop.Name, out var v))
            {
                double d = v switch
                {
                    long l => l,
                    int i => i,
                    double db => db,
                    _ => double.NaN,
                };
                if (!double.IsNaN(d))
                {
                    slider.Value = Math.Clamp(d, slider.Minimum, slider.Maximum);
                }
            }
        }
        catch { /* ignore */ }
    }

    private async Task LoadComboIndexAsync(ComboBox combo, List<ValueOption> list, PropInfo prop)
    {
        try
        {
            var values = await App.Service.ReadPropsAsync(_detail.Did, new List<PropInfo> { prop });
            if (values.TryGetValue(prop.Name, out var v))
            {
                long current = v switch
                {
                    long l => l,
                    int i => i,
                    double d => (long)d,
                    _ => long.MinValue,
                };
                var idx = list.FindIndex(o => o.Value == current);
                if (idx >= 0)
                {
                    combo.SelectedIndex = idx;
                }
            }
        }
        catch { /* ignore */ }
    }

    private async Task LoadReadOnlyAsync(TextBlock text, PropInfo prop)
    {
        try
        {
            var values = await App.Service.ReadPropsAsync(_detail.Did, new List<PropInfo> { prop });
            text.Text = values.TryGetValue(prop.Name, out var v) ? v?.ToString() ?? "—" : "—";
        }
        catch
        {
            text.Text = "读取失败";
        }
    }

    private FrameworkElement BuildActionButton(ActionInfo action)
    {
        var button = new Button
        {
            Content = action.Desc,
            MinWidth = 160,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        button.Click += async (_, _) =>
        {
            var confirm = new ContentDialog
            {
                Title = "确认执行",
                Content = $"确定要执行「{action.Desc}」吗？",
                PrimaryButtonText = "执行",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            {
                await SafeAsync($"已执行「{action.Desc}」", () => App.Service.RunActionAsync(_detail.Did, action));
            }
        };
        return button;
    }

    private async Task SafeAsync(string successMessage, Func<Task> action)
    {
        try
        {
            await action();
            App.ShowMainToast(successMessage, InfoBarSeverity.Success);
        }
        catch (Exception exc)
        {
            App.ShowMainToast(exc.Message, InfoBarSeverity.Error);
        }
    }
}
