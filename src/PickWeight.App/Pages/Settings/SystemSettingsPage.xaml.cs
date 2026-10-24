using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PickWeight_App.Controls;
using PickWeight_App.Models;
using PickWeight_App.Services;
using PickWeight_App.ViewModels;

namespace PickWeight_App.Pages.Settings;

/// <summary>系统设置二级页：开机自启、快捷启动、全局快捷键与数据入口。</summary>
public sealed partial class SystemSettingsPage : Page
{
    private SettingsPageViewModel? _viewModel;

    public SystemSettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => PlayEntrance();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        SettingsSubPageNavigation.ApplyViewModel(this, e);
        _viewModel = DataContext as SettingsPageViewModel;
        if (_viewModel is not null)
        {
            GlobalHotkeyRecorder.Hotkey = HotkeyDefinition.TryParse(_viewModel.GlobalHotkey);
            GlobalHotkeyRecorder.Recorded -= GlobalHotkeyRecorder_Recorded;
            GlobalHotkeyRecorder.Recorded += GlobalHotkeyRecorder_Recorded;
        }
    }

    private void GlobalHotkeyRecorder_Recorded(object? sender, HotkeyRecordedEventArgs e)
    {
        // 转成持久化格式（如「Ctrl+Alt+P」）交给设置服务保存
        if (_viewModel is not null)
        {
            _viewModel.GlobalHotkey = e.Hotkey?.ToString() ?? string.Empty;
        }
    }

    private void PlayEntrance()
    {
        var animations = App.GetService<AnimationService>();
        animations.PlayPageSection(HeaderPanel, 0);
        animations.PlayPageSection(ContentPanel, 1);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) =>
        SettingsSubPageNavigation.GoBack(this);

    private async void FirstRunWizard_Click(object sender, RoutedEventArgs e) =>
        await App.GetService<FirstRunWizardService>().ShowAsync(XamlRoot, markCompleted: false);

    private async void ShowLocalApiToken_Click(object sender, RoutedEventArgs e)
    {
        var tokenBox = new TextBox
        {
            Header = "X-PickWeight-Token",
            IsReadOnly = true,
            Text = App.GetService<PickWeight.Core.Abstractions.ISettingsService>().Current.LocalApiToken
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "仅供本机 OBS 或自动化脚本使用，请勿公开。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(tokenBox);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "本地 API 令牌", Content = panel, CloseButtonText = "关闭" };
        await dialog.ShowAsync();
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PickWeight");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var destination = await App.GetService<FileDialogService>().SavePathAsync("PickWeight-诊断包", ".zip");
        if (destination is null)
        {
            return;
        }

        try
        {
            var settings = App.GetService<PickWeight.Core.Abstractions.ISettingsService>().Current.Clone();
            await App.GetService<AppLogService>().ExportDiagnosticsAsync(settings, destination);
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "诊断包已导出",
                Content = destination,
                CloseButtonText = "确定"
            }.ShowAsync();
        }
        catch (Exception exception)
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "诊断包导出失败",
                Content = exception.Message,
                CloseButtonText = "确定"
            }.ShowAsync();
        }
    }
}
