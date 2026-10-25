using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MiHome.Core;
using MiHome.Core.Models;
using MiHome.Core.Stores;

namespace MiHome.App.Views;

/// <summary>设置页：主题、托盘、自启动、语音、更新。</summary>
public sealed partial class SettingsPage : Page
{
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        LoadSettings();
        _loading = false;
    }

    private void LoadSettings()
    {
        var s = App.Settings;
        foreach (ComboBoxItem item in ThemeCombo.Items)
        {
            if ((string)item.Tag == s.Theme)
            {
                ThemeCombo.SelectedItem = item;
                break;
            }
        }
        var accents = new (string Name, string Hex)[]
        {
            ("米家绿（默认）", ""),
            ("粉", "#E91E63"),
            ("黄", "#F9A825"),
            ("蓝", "#2196F3"),
            ("紫", "#7E57C2"),
        };
        AccentCombo.ItemsSource = accents.Select(a => a.Name).ToList();
        AccentCombo.SelectedIndex = Array.FindIndex(accents, a => string.Equals(a.Hex, s.ThemeAccent, StringComparison.OrdinalIgnoreCase));
        AccentCombo.Tag = accents;

        MinimizeTrayToggle.IsOn = s.MinimizeToTray;
        StartMinimizedToggle.IsOn = s.StartMinimized;
        AutoRefreshToggle.IsOn = s.AutoRefresh;
        HideNoFuncToggle.IsOn = s.HideNoFuncDevices;
        CheckUpdateToggle.IsOn = s.CheckUpdateEnabled;
        AutostartToggle.IsOn = Autostart.Get();

        _ = LoadSpeakersAsync();
    }

    private async Task LoadSpeakersAsync()
    {
        try
        {
            var devices = await App.Service.ListDevicesAsync();
            var speakers = devices.Where(d => MiHomeService.IsSpeaker(d)).ToList();
            var names = new List<string> { "自动（第一个在线音箱）" };
            names.AddRange(speakers.Select(d => d.Name));
            SpeakerCombo.ItemsSource = names;
            SpeakerCombo.Tag = speakers;
            var current = s_defaultSpeaker;
            if (string.IsNullOrEmpty(current))
            {
                SpeakerCombo.SelectedIndex = 0;
            }
            else
            {
                var idx = speakers.FindIndex(d => d.Did == current);
                SpeakerCombo.SelectedIndex = idx >= 0 ? idx + 1 : 0;
            }
        }
        catch
        {
            SpeakerCombo.ItemsSource = new List<string> { "自动（第一个在线音箱）" };
            SpeakerCombo.SelectedIndex = 0;
        }
    }

    private static string s_defaultSpeaker => App.Settings.DefaultSpeakerDid;

    private void Save() => App.SaveSettings();

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }
        App.Settings.Theme = (string)item.Tag;
        Save();
        App.ApplyTheme();
    }

    private void Accent_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AccentCombo.Tag is not (string Name, string Hex)[] accents || AccentCombo.SelectedIndex < 0)
        {
            return;
        }
        App.Settings.ThemeAccent = accents[AccentCombo.SelectedIndex].Hex;
        Save();
    }

    private void MinimizeTray_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.MinimizeToTray = MinimizeTrayToggle.IsOn;
        Save();
    }

    private void StartMinimized_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.StartMinimized = StartMinimizedToggle.IsOn;
        Save();
    }

    private void AutoRefresh_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.AutoRefresh = AutoRefreshToggle.IsOn;
        Save();
        App.ShowMainToast(AutoRefreshToggle.IsOn ? "自动刷新已开启（重新进入设备页生效）" : "自动刷新已关闭",
            InfoBarSeverity.Informational);
    }

    private void HideNoFunc_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.HideNoFuncDevices = HideNoFuncToggle.IsOn;
        Save();
    }

    private void Autostart_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            Autostart.Set(AutostartToggle.IsOn);
        }
        catch (Exception exc)
        {
            App.ShowMainToast($"设置自启动失败：{exc.Message}", InfoBarSeverity.Error);
        }
    }

    private void Speaker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (SpeakerCombo.Tag is List<DeviceInfo> speakers && SpeakerCombo.SelectedIndex > 0)
        {
            App.Settings.DefaultSpeakerDid = speakers[SpeakerCombo.SelectedIndex - 1].Did;
        }
        else
        {
            App.Settings.DefaultSpeakerDid = "";
        }
        Save();
    }

    private void CheckUpdate_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.CheckUpdateEnabled = CheckUpdateToggle.IsOn;
        Save();
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateText.Text = "正在检查…";
        var info = await UpdateChecker.CheckAsync();
        UpdateText.Text = info == null
            ? $"已是最新版本（v{UpdateChecker.CurrentVersion}）"
            : $"发现新版本 {info.LatestVersion}，请到 GitHub 下载";
        if (info != null)
        {
            App.ShowMainToast($"发现新版本 {info.LatestVersion}", InfoBarSeverity.Informational);
        }
    }
}
