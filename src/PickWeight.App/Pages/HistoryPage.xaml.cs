using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PickWeight_App.Services;
using PickWeight_App.ViewModels;

namespace PickWeight_App.Pages;

public sealed partial class HistoryPage : Page
{
    public HistoryPage()
    {
        InitializeComponent();
        DataContext = App.GetService<HistoryPageViewModel>();
        Loaded += (_, _) => PlayPageEntrance();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // ViewModel 是 transient，离开页面必须退订单例事件，否则每次导航泄漏一个实例
        (DataContext as HistoryPageViewModel)?.Detach();
        base.OnNavigatedFrom(e);
    }

    private void PlayPageEntrance()
    {
        var animations = App.GetService<AnimationService>();
        var sections = new UIElement[]
        {
            HeaderSection,
            CommandSection,
            ArchiveSection,
            SummarySection,
            StatusSection,
            HistoryContentSection
        };
        for (var index = 0; index < sections.Length; index++)
        {
            animations.PlayPageSection(sections[index], index);
        }
    }

    private void HistoryList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        PlayListItem(sender, args);

    private void StatisticsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        PlayListItem(sender, args);

    private async void ShowAllMembers_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not HistoryPageViewModel viewModel) return;
        var list = new ListView
        {
            ItemsSource = viewModel.MemberStatistics,
            SelectionMode = ListViewSelectionMode.None,
            MaxHeight = 520
        };
        list.ItemTemplate = CreateMemberTemplate();
        await ShowStatisticsDialogAsync("全部成员排行", list);
    }

    private async void ShowNeverDrawn_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not HistoryPageViewModel viewModel) return;
        var list = new ListView
        {
            ItemsSource = viewModel.NeverDrawnMembers,
            SelectionMode = ListViewSelectionMode.None,
            MaxHeight = 520
        };
        await ShowStatisticsDialogAsync("尚未抽取成员", list);
    }

    private async Task ShowStatisticsDialogAsync(string title, UIElement content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            CloseButtonText = "关闭"
        };
        dialog.Opened += (_, _) => App.GetService<AnimationService>().PlayPopupEntrance(dialog);
        await dialog.ShowAsync();
    }

    private static DataTemplate CreateMemberTemplate() =>
        (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid Padding='8,6' ColumnSpacing='8'><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions><TextBlock Text='{Binding Name}'/><TextBlock Grid.Column='1' Text='{Binding DrawCount}'/></Grid></DataTemplate>");

    private static void PlayListItem(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue
            && args.ItemContainer is ListViewItem item
            && item.ContentTemplateRoot is UIElement content)
        {
            App.GetService<AnimationService>().PlayListItem(content, Math.Max(sender.IndexFromContainer(item), 0));
        }
    }
}
