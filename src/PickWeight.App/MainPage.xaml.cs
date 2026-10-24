using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PickWeight_App.Pages;
using PickWeight_App.Services;
using PickWeight_App.ViewModels;

namespace PickWeight_App;

/// <summary>
/// 主壳层只负责导航和页面离开确认，业务状态全部保留在 ViewModel 与服务中。
/// </summary>
public sealed partial class MainPage : Page
{
    private bool _restoringSelection;
    private NavigationViewItem? _currentItem;
    private List<AppDataService.ClassroomSummary> _classroomSummaries = [];

    public MainPage()
    {
        InitializeComponent();

        // 在导航菜单首位插入「返回」项。此处在代码中创建而非写在 XAML，
        // 以避开 XamlCompiler 对 6 项 NavigationView.MenuItems 的编译崩溃。
        var backItem = new NavigationViewItem
        {
            Content = "返回",
            Icon = new SymbolIcon(Symbol.Back),
            SelectsOnInvoked = false,
            Tag = "back"
        };
        backItem.Tapped += BackButton_Click;
        Navigation.MenuItems.Insert(0, backItem);

        // 恢复上次的导航窗格常驻/折叠状态，并把后续折叠/展开动作落盘
        Navigation.IsPaneOpen = App.GetService<PickWeight.Core.Abstractions.ISettingsService>()
            .Current.IsNavigationPaneOpen;
        Navigation.PaneOpened += async (_, _) => await SavePaneStateAsync(open: true);
        Navigation.PaneClosed += async (_, _) => await SavePaneStateAsync(open: false);

        Loaded += async (_, _) =>
        {
            _currentItem = DrawMenuItem();
            Navigation.SelectedItem = _currentItem;
            ContentFrame.Navigate(typeof(DrawPage));
            await RefreshClassroomUiAsync();
        };
    }

    private async Task SavePaneStateAsync(bool open)
    {
        try
        {
            await App.GetService<PickWeight.Core.Abstractions.ISettingsService>()
                .UpdateAsync(settings => settings.IsNavigationPaneOpen = open);
        }
        catch
        {
            // 窗格状态保存失败不影响使用
        }
    }

    private NavigationViewItem? DrawMenuItem() =>
        Navigation.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, "draw", StringComparison.Ordinal));

    /// <summary>「返回」按钮：确认未保存修改后回到点名页。</summary>
    private async void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is DrawPage)
        {
            return;
        }

        if (!await ConfirmPageExitAsync(releasePageResources: true))
        {
            return;
        }

        if (DrawMenuItem() is { } drawItem)
        {
            _restoringSelection = true;
            Navigation.SelectedItem = drawItem;
            _restoringSelection = false;
            _currentItem = drawItem;
        }

        if (ContentFrame.Content is not DrawPage)
        {
            ContentFrame.Navigate(typeof(DrawPage));
        }
    }

    private async void ClassroomSwitcher_Click(object sender, object e)
    {
        // 每次打开下拉前重新加载班级列表，保证与磁盘同步
        var data = App.GetService<AppDataService>();
        _classroomSummaries = (await data.ListClassroomSummariesAsync()).ToList();

        if (ClassroomSwitcher.Flyout is MenuFlyout flyout)
        {
            flyout.Items.Clear();
            foreach (var summary in _classroomSummaries)
            {
                var item = new MenuFlyoutItem
                {
                    Text = string.Equals(summary.Name, data.State.ActiveClassroomName, StringComparison.Ordinal)
                        ? $"{summary.Name}（当前）"
                        : summary.Name,
                    Tag = summary.Name
                };
                item.Click += async (_, _) => await SwitchClassroomAsync((string)item.Tag);
                flyout.Items.Add(item);
            }

            flyout.Items.Add(new MenuFlyoutSeparator());
            var manageItem = new MenuFlyoutItem { Text = "管理班级…", Icon = new FontIcon { Glyph = "&#xE8FD;" } };
            manageItem.Click += async (_, _) => await ShowManageClassroomsDialogAsync();
            flyout.Items.Add(manageItem);
        }
    }

    private Task RefreshClassroomUiAsync()
    {
        var data = App.GetService<AppDataService>();
        ClassroomNameText.Text = data.State.ActiveClassroomName;
        ClassroomLabel.Visibility = Visibility.Visible;
        return Task.CompletedTask;
    }

    private async Task SwitchClassroomAsync(string name)
    {
        var data = App.GetService<AppDataService>();
        if (string.Equals(name, data.State.ActiveClassroomName, StringComparison.Ordinal))
        {
            return;
        }

        // 离开前确认未保存的页面编辑
        if (!await ConfirmPageExitAsync(releasePageResources: true))
        {
            return;
        }

        try
        {
            await data.SwitchClassroomAsync(name);
        }
        catch (Exception exception)
        {
            await App.GetService<DialogService>().ShowAsync("切换班级失败", exception.Message);
            return;
        }

        // 班级切换后，所有页面的数据源都变了，需要重新加载
        if (ContentFrame.Content is DrawPage drawPage)
        {
            await drawPage.PrepareForNavigationAsync();
            ContentFrame.Navigate(typeof(DrawPage));
        }
        else
        {
            NavigateCurrentPageFresh();
        }

        await RefreshClassroomUiAsync();
    }

    private void NavigateCurrentPageFresh()
    {
        if (ContentFrame.Content is HistoryPage)
        {
            ContentFrame.Navigate(typeof(HistoryPage));
        }
        else if (ContentFrame.Content is MembersPage)
        {
            ContentFrame.Navigate(typeof(MembersPage));
        }
        else if (ContentFrame.Content is GroupsPage)
        {
            ContentFrame.Navigate(typeof(GroupsPage));
        }
        else if (ContentFrame.Content is SeatMapPage)
        {
            ContentFrame.Navigate(typeof(SeatMapPage));
        }
    }

    private async Task ShowManageClassroomsDialogAsync()
    {
        var data = App.GetService<AppDataService>();
        var summaries = (await data.ListClassroomSummariesAsync()).ToList();

        var listBox = new ListBox
        {
            MaxHeight = 320,
            ItemsSource = summaries,
            DisplayMemberPath = "Name",
            SelectionMode = SelectionMode.Single
        };
        if (summaries.Count > 0)
        {
            listBox.SelectedIndex = 0;
        }

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(listBox);

        var nameBox = new TextBox { PlaceholderText = "输入班级名称", Header = "班级名称" };

        var subjectBox = new TextBox { PlaceholderText = "例如：语文（可选）", Header = "科目" };

        // 选中班级时回填科目
        listBox.SelectionChanged += (_, _) =>
        {
            if (listBox.SelectedItem is AppDataService.ClassroomSummary selected)
            {
                subjectBox.Text = selected.Subject ?? string.Empty;
            }
        };
        if (summaries.FirstOrDefault() is { } first)
        {
            subjectBox.Text = first.Subject ?? string.Empty;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "管理班级",
            Content = panel,
            PrimaryButtonText = "关闭",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var actionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var addButton = new Button { Content = "新建" };
        addButton.Click += async (_, _) =>
        {
            var newName = nameBox.Text?.Trim() ?? string.Empty;
            try
            {
                await data.CreateClassroomAsync(newName);
            }
            catch (Exception exception)
            {
                await App.GetService<DialogService>().ShowAsync("新建班级失败", exception.Message);
                return;
            }

            await RefreshClassroomUiAsync();
            // 切换页面到新班级
            if (ContentFrame.Content is DrawPage drawPage)
            {
                await drawPage.PrepareForNavigationAsync();
                ContentFrame.Navigate(typeof(DrawPage));
            }
            else
            {
                NavigateCurrentPageFresh();
            }
        };
        actionsPanel.Children.Add(addButton);

        var renameButton = new Button { Content = "重命名" };
        renameButton.Click += async (_, _) =>
        {
            if (listBox.SelectedItem is not AppDataService.ClassroomSummary selected)
            {
                return;
            }

            var newName = nameBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(newName))
            {
                await App.GetService<DialogService>().ShowAsync("重命名班级失败", "班级名称不能为空。");
                return;
            }

            try
            {
                await data.RenameClassroomAsync(selected.Name, newName);
            }
            catch (Exception exception)
            {
                await App.GetService<DialogService>().ShowAsync("重命名班级失败", exception.Message);
                return;
            }

            await RefreshClassroomUiAsync();
            NavigateCurrentPageFresh();
        };
        actionsPanel.Children.Add(renameButton);

        var deleteButton = new Button { Content = "删除" };
        deleteButton.Click += async (_, _) =>
        {
            if (listBox.SelectedItem is not AppDataService.ClassroomSummary selected)
            {
                return;
            }

            if (!await App.GetService<DialogService>().ConfirmAsync(
                "删除班级",
                $"确定删除班级“{selected.Name}”吗？将删除该班的全部数据（名单、历史、配置），不可恢复。",
                "删除"))
            {
                return;
            }

            try
            {
                await data.DeleteClassroomAsync(selected.Name);
            }
            catch (Exception exception)
            {
                await App.GetService<DialogService>().ShowAsync("删除班级失败", exception.Message);
                return;
            }

            await RefreshClassroomUiAsync();
            NavigateCurrentPageFresh();
        };
        actionsPanel.Children.Add(deleteButton);

        var subjectButton = new Button { Content = "保存科目" };
        subjectButton.Click += async (_, _) =>
        {
            if (listBox.SelectedItem is not AppDataService.ClassroomSummary selected)
            {
                return;
            }

            try
            {
                await data.UpdateClassroomSubjectAsync(selected.Name, subjectBox.Text);
            }
            catch (Exception exception)
            {
                await App.GetService<DialogService>().ShowAsync("保存科目失败", exception.Message);
                return;
            }

            await RefreshClassroomUiAsync();
        };
        actionsPanel.Children.Add(subjectButton);

        panel.Children.Add(nameBox);
        panel.Children.Add(subjectBox);
        panel.Children.Add(actionsPanel);

        await dialog.ShowAsync();
    }

    public async Task NavigateToDrawAndDrawAsync()
    {
        if (!await ConfirmPageExitAsync(releasePageResources: ContentFrame.Content is not DrawPage))
        {
            return;
        }

        if (DrawMenuItem() is { } drawItem)
        {
            _restoringSelection = true;
            Navigation.SelectedItem = drawItem;
            _restoringSelection = false;
            _currentItem = drawItem;
        }

        if (ContentFrame.Content is not DrawPage)
        {
            ContentFrame.Navigate(typeof(DrawPage));
        }

        if (ContentFrame.Content is DrawPage drawPage
            && drawPage.DataContext is DrawPageViewModel viewModel)
        {
            await viewModel.DrawCommand.ExecuteAsync(null);
        }
    }

    public async Task NavigateToSeatMapAsync(IReadOnlyList<string> highlightNames)
    {
        if (!await ConfirmPageExitAsync(releasePageResources: ContentFrame.Content is not SeatMapPage))
        {
            return;
        }

        var seatsItem = Navigation.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, "seats", StringComparison.Ordinal));
        if (seatsItem is not null)
        {
            _restoringSelection = true;
            Navigation.SelectedItem = seatsItem;
            _restoringSelection = false;
            _currentItem = seatsItem;
        }

        ContentFrame.Navigate(typeof(SeatMapPage), highlightNames);
    }

    private async Task<bool> ConfirmPageExitAsync(bool releasePageResources = false)
    {
        if (ContentFrame.Content is not FrameworkElement element)
        {
            return true;
        }

        if (element.DataContext is IEditablePageViewModel editable && editable.IsDirty)
        {
            var choice = await App.GetService<DialogService>().ConfirmUnsavedAsync();
            if (choice == UnsavedChoice.Cancel)
            {
                return false;
            }

            if (choice == UnsavedChoice.Save)
            {
                await editable.SaveAsync();
            }
        }

        return !releasePageResources || await PrepareCurrentPageForNavigationAsync();
    }

    private async Task<bool> PrepareCurrentPageForNavigationAsync()
    {
        if (ContentFrame.Content is not DrawPage drawPage)
        {
            return true;
        }

        try
        {
            await drawPage.PrepareForNavigationAsync();
            return true;
        }
        catch (Exception exception)
        {
            await App.GetService<DialogService>().ShowAsync(
                "无法离开抽取页面",
                $"清理未完成抽取失败（{exception.GetType().Name}）。请重试。");
            return false;
        }
    }

    private async void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_restoringSelection)
        {
            return;
        }
        NavigationViewItem? selected = args.IsSettingsSelected ? null : args.SelectedItemContainer as NavigationViewItem;
        if (!args.IsSettingsSelected && (selected is null || selected == _currentItem)) return;

        if (!await ConfirmPageExitAsync(releasePageResources: true))
        {
            _restoringSelection = true;
            Navigation.SelectedItem = _currentItem;
            _restoringSelection = false;
            return;
        }

        _currentItem = selected;
        ContentFrame.Navigate(args.IsSettingsSelected ? typeof(SettingsPage) : selected?.Tag?.ToString() switch
        {
            "history" => typeof(HistoryPage),
            "members" => typeof(MembersPage),
            "groups" => typeof(GroupsPage),
            "seats" => typeof(SeatMapPage),
            _ => typeof(DrawPage)
        });
    }
}
