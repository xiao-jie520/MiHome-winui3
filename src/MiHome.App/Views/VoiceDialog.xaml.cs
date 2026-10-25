using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MiHome.Core;
using MiHome.Core.Models;

namespace MiHome.App.Views;

/// <summary>小爱语音对话框：输入文本指令发给在线小爱音箱。</summary>
public sealed class VoiceDialog : ContentDialog
{
    private readonly List<DeviceInfo> _speakers;
    private readonly TextBox _input = new()
    {
        Header = "文本指令",
        PlaceholderText = "例如：把客厅灯打开",
    };
    private readonly ComboBox _speakerCombo = new()
    {
        Header = "输出音箱",
        MinWidth = 280,
        Margin = new Thickness(0, 12, 0, 0),
    };

    public VoiceDialog(List<DeviceInfo> speakers)
    {
        _speakers = speakers;
        Title = "小爱语音";
        PrimaryButtonText = "发送";
        CloseButtonText = "取消";
        DefaultButton = ContentDialogButton.Primary;

        var names = speakers.Select(d => d.Name).ToList();
        if (names.Count == 0)
        {
            names.Add("未找到在线音箱");
            IsPrimaryButtonEnabled = false;
        }
        _speakerCombo.ItemsSource = names;
        // 默认输出：设置里的音箱若仍在线则选中它，否则第一台
        var defaultDid = App.Settings.DefaultSpeakerDid;
        var idx = speakers.FindIndex(d => d.Did == defaultDid);
        _speakerCombo.SelectedIndex = idx >= 0 ? idx : 0;

        var panel = new StackPanel { MinWidth = 360 };
        panel.Children.Add(_input);
        panel.Children.Add(_speakerCombo);
        Content = panel;

        PrimaryButtonClick += OnPrimary;
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var text = _input.Text.Trim();
        var idx = _speakerCombo.SelectedIndex;
        if (text.Length == 0 || idx < 0 || idx >= _speakers.Count)
        {
            args.Cancel = true;
            return;
        }
        var speaker = _speakers[idx];
        var action = new ActionInfo(
            Name: "execute-text-directive",
            Desc: "执行文本指令",
            Siid: 7,
            Aiid: 1,
            InParams: null);
        var deferral = args.GetDeferral();
        _ = Task.Run(async () =>
        {
            try
            {
                await App.Service.RunActionAsync(speaker.Did, action, new object[] { text, 0 });
                App.ShowMainToast($"指令已发送给「{speaker.Name}」", InfoBarSeverity.Success);
            }
            catch (Exception exc)
            {
                App.ShowMainToast($"发送失败：{exc.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                deferral.Complete();
            }
        });
    }
}
