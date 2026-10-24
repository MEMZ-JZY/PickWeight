using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PickWeight_App.Services;
using PickWeight_App.ViewModels;
using Windows.System;

namespace PickWeight_App.Pages;

public sealed partial class DrawPage : Page
{
    private readonly AnimationService _animations;
    private readonly HashSet<UIElement> _animatedItems = [];
    private readonly DrawPageViewModel _viewModel;

    public DrawPage()
    {
        InitializeComponent();
        _animations = App.GetService<AnimationService>();
        _viewModel = App.GetService<DrawPageViewModel>();
        DataContext = _viewModel;
        _viewModel.DrawStarted += BeginResultAnimation;
        _viewModel.DrawCompleted += AnimateRealizedResults;
        _viewModel.InitializeAttendance();
        Loaded += (_, _) => PlayPageEntrance();
    }

    internal Task PrepareForNavigationAsync() => _viewModel.OnNavigatedFromAsync();

    /// <summary>
    /// 键盘快捷键：空格/回车抽取，1-4 切换模式（写入设置立即生效），+/- 调整人数，R 换一个。
    /// 焦点位于输入控件内时不拦截按键，避免影响正常输入。
    /// </summary>
    private async void PageRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsFocusInInputControl())
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Space:
            case VirtualKey.Enter:
                e.Handled = true;
                _viewModel.DrawCommand.Execute(null);
                break;
            case VirtualKey.Number1:
            case VirtualKey.Number2:
            case VirtualKey.Number3:
            case VirtualKey.Number4:
                e.Handled = true;
                await _viewModel.ApplyDrawModeShortcutAsync((int)(e.Key - VirtualKey.Number1));
                break;
            case VirtualKey.Add:
            case (VirtualKey)187: // '+' 键
                e.Handled = true;
                _viewModel.TargetCount = Math.Max(1d, _viewModel.TargetCount + 1d);
                break;
            case VirtualKey.Subtract:
                e.Handled = true;
                _viewModel.TargetCount = Math.Max(1d, _viewModel.TargetCount - 1d);
                break;
            case VirtualKey.R:
                e.Handled = true;
                var pending = _viewModel.Results.FirstOrDefault(item => !item.IsCommitted && !item.IsDiscarded);
                if (pending is not null)
                {
                    pending.ReplaceCommand.Execute(null);
                }

                break;
        }
    }

    /// <summary>判断当前焦点是否位于输入类控件（文本框、数字框、组合框、开关、按钮等）。</summary>
    private static bool IsFocusInInputControl()
    {
        var focused = FocusManager.GetFocusedElement() as Control;
        return focused is TextBox or NumberBox or ComboBox or ToggleSwitch or PasswordBox or Button
            or Slider or RichEditBox or AutoSuggestBox or ContentDialog;
    }

    /// <summary>公平性提醒的关闭按钮 = 忽略本次会话的提醒。</summary>
    private void FairnessReminder_CloseButtonClick(InfoBar sender, object args)
    {
        _viewModel.DismissFairnessReminder();
    }

    private async void RulesButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.BeginRuleEdit();
        RulesDialog.DataContext = _viewModel.RuleDraft;
        RulesDialog.XamlRoot = XamlRoot;
        await RulesDialog.ShowAsync();
    }

    private async void RulesDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            await _viewModel.ApplyRuleDraftAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void RulesDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args) =>
        _viewModel.CancelRuleEdit();

    private void RulesDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        _animations.PlayPopupEntrance(sender);
    }

    private async void ApplyPreset_Click(object sender, RoutedEventArgs e) => await _viewModel.ApplySelectedPresetAsync();

    private async void DeletePreset_Click(object sender, RoutedEventArgs e) => await _viewModel.DeleteSelectedPresetAsync();

    private async void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { Header = "预设名称", PlaceholderText = "例如：课堂提问" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "保存抽取预设",
            Content = nameBox,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _viewModel.SavePresetAsync(nameBox.Text);
        }
        catch (ArgumentException exception)
        {
            await App.GetService<DialogService>().ShowAsync("无法保存预设", exception.Message);
        }
    }

    private async void ViewSeatMap_Click(object sender, RoutedEventArgs e)
    {
        var names = _viewModel.Results
            .Where(row => !row.IsDiscarded)
            .Select(row => row.Name)
            .ToList();
        if (names.Count == 0)
        {
            return;
        }

        if (App.Window is MainWindow mainWindow)
        {
            await mainWindow.NavigateToSeatMapAsync(names);
        }
    }

    private void PlayPageEntrance()
    {
        _animations.PlayPageSection(HeaderSection, 0);
        _animations.PlayPageSection(ActionSection, 1);
        _animations.PlayPageSection(StatusSection, 2);
        _animations.PlayPageSection(ResultsList, 3);
    }

    private void ResultsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue &&
            args.ItemContainer is ListViewItem item &&
            item.ContentTemplateRoot is UIElement content)
        {
            AnimateResult(sender, item, content);
        }
    }

    private void BeginResultAnimation()
    {
        foreach (var item in _animatedItems)
        {
            _animations.Stop(item);
        }

        _animatedItems.Clear();
    }

    private void AnimateRealizedResults()
    {
        for (var index = 0; index < ResultsList.Items.Count; index++)
        {
            if (ResultsList.ContainerFromIndex(index) is ListViewItem item && item.ContentTemplateRoot is UIElement content)
            {
                AnimateResult(ResultsList, item, content);
            }
        }
    }

    private void AnimateResult(ListViewBase list, ListViewItem item, UIElement content)
    {
        if (_animatedItems.Add(content))
        {
            _animations.PlayResultReveal(content, Math.Max(list.IndexFromContainer(item), 0));
        }
    }
}
