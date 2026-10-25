using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MiHome.Core.Models;

namespace MiHome.App.Views;

/// <summary>场景页：手动场景一键执行。</summary>
public sealed partial class ScenesPage : Page
{
    private bool _loaded;

    public ScenesPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => { if (!_loaded) { _loaded = true; await ReloadAsync(); } };
    }

    private async Task ReloadAsync()
    {
        Ring.IsActive = true;
        ListPanel.Children.Clear();
        try
        {
            if (!App.Service.LoginStatus())
            {
                ListPanel.Children.Add(new TextBlock { Text = "尚未登录，请先在设备页扫码登录。" });
                return;
            }
            var scenes = await App.Service.SceneListAsync();
            if (scenes.Count == 0)
            {
                ListPanel.Children.Add(new TextBlock
                {
                    Text = "没有手动场景。可在米家 APP → 智能 → 手动控制 中创建。",
                    Opacity = 0.7,
                });
                return;
            }
            foreach (var scene in scenes)
            {
                ListPanel.Children.Add(BuildCard(scene));
            }
        }
        catch (Exception exc)
        {
            ListPanel.Children.Add(new TextBlock { Text = $"获取场景失败：{exc.Message}", Foreground = GetErrorBrush() });
        }
        finally
        {
            Ring.IsActive = false;
        }
    }

    private static SolidColorBrush GetErrorBrush() =>
        (SolidColorBrush)Application.Current.Resources["SystemFillColorCriticalBrush"];

    private Border BuildCard(SceneInfo scene)
    {
        var title = new TextBlock
        {
            Text = scene.Name,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        var subtitle = new TextBlock
        {
            Text = string.IsNullOrEmpty(scene.HomeName) ? "手动场景" : scene.HomeName,
            FontSize = 12,
            Opacity = 0.65,
        };
        var runButton = new Button { Content = "执行" };
        runButton.Click += async (_, _) =>
        {
            try
            {
                runButton.IsEnabled = false;
                await App.Service.RunSceneAsync(scene);
                App.ShowMainToast($"场景「{scene.Name}」已执行", InfoBarSeverity.Success);
            }
            catch (Exception exc)
            {
                App.ShowMainToast(exc.Message, InfoBarSeverity.Error);
            }
            finally
            {
                runButton.IsEnabled = true;
            }
        };

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var textStack = new StackPanel { Spacing = 2 };
        textStack.Children.Add(title);
        textStack.Children.Add(subtitle);
        Grid.SetColumn(textStack, 0);
        Grid.SetColumn(runButton, 1);
        grid.Children.Add(textStack);
        grid.Children.Add(runButton);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();
}
