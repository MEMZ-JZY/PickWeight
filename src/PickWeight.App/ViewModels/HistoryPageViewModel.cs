using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PickWeight.Core.Abstractions;
using PickWeight.Core.Models;
using PickWeight.Core.Services;
using PickWeight_App.Services;

namespace PickWeight_App.ViewModels;

public partial class HistoryPageViewModel : ObservableObject
{
    private readonly AppDataService _data;
    private readonly DialogService _dialogs;
    private readonly FileDialogService _files;
    private readonly IHistoryExportService _historyExport;
    private List<DrawHistoryEntry> _allHistory = [];
    private List<DrawHistoryEntry> _filteredHistory = [];
    private bool _isReloading;
    private int _reloadVersion;

    public ObservableCollection<DrawHistoryRowViewModel> History { get; } = [];
    public ObservableCollection<OverviewStatisticRowViewModel> OverviewStatistics { get; } = [];
    public ObservableCollection<MemberStatisticRowViewModel> MemberStatistics { get; } = [];
    public ObservableCollection<MemberStatisticRowViewModel> TopMemberStatistics { get; } = [];
    public ObservableCollection<GroupStatisticRowViewModel> GroupStatistics { get; } = [];
    public ObservableCollection<DailyTrendStatisticRowViewModel> DailyTrendStatistics { get; } = [];
    public ObservableCollection<string> NeverDrawnMembers { get; } = [];
    public ObservableCollection<MemberRecencyStatisticRowViewModel> StaleMembers { get; } = [];
    public ObservableCollection<string> MemberFilterOptions { get; } = [];
    public ObservableCollection<string> GroupFilterOptions { get; } = [];

    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private InfoBarSeverity statusSeverity = InfoBarSeverity.Success;
    [ObservableProperty] private string totalDrawCountText = "0";
    [ObservableProperty] private string drawnMemberCountText = "0";
    [ObservableProperty] private string neverDrawnCountText = "0";
    [ObservableProperty] private string latestDrawText = "暂无";
    [ObservableProperty] private DateTimeOffset? startDate;
    [ObservableProperty] private DateTimeOffset? endDate;
    [ObservableProperty] private int selectedMemberFilterIndex;
    [ObservableProperty] private int selectedGroupFilterIndex;
    [ObservableProperty] private string emptyStateTitle = "暂无抽取历史";
    [ObservableProperty] private string emptyStateDescription = "完成一次点名后，这里会显示记录和统计。";
    [ObservableProperty] private bool includeArchived;
    [ObservableProperty] private double fairnessIndex;
    [ObservableProperty] private string fairnessDescription = string.Empty;
    [ObservableProperty] private string fairnessIndexText = "100";

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool HasAnyHistory => _allHistory.Count > 0;
    public bool HasFilteredHistory => History.Count > 0;
    public bool HasActiveFilters => StartDate is not null || EndDate is not null || SelectedMemberFilterIndex > 0 || SelectedGroupFilterIndex > 0;
    public Visibility ClearEmptyFiltersVisibility => HasAnyHistory && HasActiveFilters ? Visibility.Visible : Visibility.Collapsed;
    public bool HasMemberStatistics => MemberStatistics.Count > 0;
    public bool HasGroupStatistics => GroupStatistics.Count > 0;
    public bool HasStatistics => OverviewStatistics.Count > 0;
    public bool HasTrendData => DailyTrendStatistics.Count > 0;
    public bool HasNeverDrawnMembers => NeverDrawnMembers.Count > 0;
    public bool HasStaleMembers => StaleMembers.Count > 0;
    public Visibility MemberStatisticsVisibility => HasMemberStatistics ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GroupStatisticsVisibility => HasGroupStatistics ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NeverDrawnMembersVisibility => HasNeverDrawnMembers ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StaleMembersVisibility => HasStaleMembers ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HistoryListVisibility => HasFilteredHistory ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyStateVisibility => HasFilteredHistory ? Visibility.Collapsed : Visibility.Visible;

    public HistoryPageViewModel(
        AppDataService data,
        DialogService dialogs,
        FileDialogService files,
        IHistoryExportService historyExport)
    {
        _data = data;
        _dialogs = dialogs;
        _files = files;
        _historyExport = historyExport;
        // ReloadAsync 的订阅发生在首个 await 之前，构造时即完成注册
        _ = ReloadAsync();
    }

    /// <summary>归档完成后刷新数据，让勾选「包含归档数据」的统计保持最新。</summary>
    private void Data_HistoryArchived(object? sender, EventArgs e)
    {
        if (IncludeArchived)
        {
            _ = ReloadAsync();
        }
    }

    /// <summary>
    /// 离开页面时退订单例事件。本 ViewModel 为 transient，若不退订，
    /// 每次导航都会向单例 AppDataService 泄漏一个持有全部历史克隆的实例。
    /// </summary>
    public void Detach() => _data.HistoryArchived -= Data_HistoryArchived;

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));
    partial void OnStartDateChanged(DateTimeOffset? value) => ApplyFilters();
    partial void OnEndDateChanged(DateTimeOffset? value) => ApplyFilters();
    partial void OnSelectedMemberFilterIndexChanged(int value) => ApplyFilters();
    partial void OnSelectedGroupFilterIndexChanged(int value) => ApplyFilters();
    partial void OnIncludeArchivedChanged(bool value) => _ = ReloadAsync();

    public void Reload() => _ = ReloadAsync();

    /// <summary>
    /// 异步重建历史与统计。归档读取不再阻塞 UI 线程（同步等待异步 IO 在
    /// UI 线程上存在死锁风险）；并发触发时旧任务按版本号自动作废。
    /// </summary>
    public async Task ReloadAsync()
    {
        var version = ++_reloadVersion;
        _data.HistoryArchived -= Data_HistoryArchived;
        _data.HistoryArchived += Data_HistoryArchived;
        List<DrawHistoryEntry>? archived = null;
        if (IncludeArchived)
        {
            try
            {
                // 不用 ConfigureAwait(false)：后续要更新绑定到 UI 的集合，
                // 必须留在 UI 线程；死锁风险已通过消除同步等待解决。
                archived = (await _data.LoadArchivedHistoryAsync()).ToList();
            }
            catch
            {
                // 归档读取失败不应让历史页卡死，仅展示主文件历史
                archived = [];
            }
        }

        if (version != _reloadVersion)
        {
            return;
        }

        _isReloading = true;
        try
        {
            var source = IncludeArchived
                ? (archived ?? []).Concat(_data.ActiveClassroom.DrawHistory)
                : _data.ActiveClassroom.DrawHistory;
            _allHistory = source
                .OrderByDescending(entry => entry.DrawnAt)
                .Select(entry => entry.Clone())
                .ToList();
            RebuildFilterOptions();
            SelectedMemberFilterIndex = Math.Clamp(SelectedMemberFilterIndex, 0, Math.Max(0, MemberFilterOptions.Count - 1));
            SelectedGroupFilterIndex = Math.Clamp(SelectedGroupFilterIndex, 0, Math.Max(0, GroupFilterOptions.Count - 1));
        }
        finally
        {
            _isReloading = false;
        }

        ApplyFilters();
    }

    private void RebuildFilterOptions()
    {
        MemberFilterOptions.Clear();
        MemberFilterOptions.Add("全部成员");
        foreach (var name in _allHistory
            .SelectMany(entry => entry.MemberNames)
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            MemberFilterOptions.Add(name);
        }

        GroupFilterOptions.Clear();
        GroupFilterOptions.Add("全部小组");
        foreach (var groupName in _data.ActiveClassroom.Groups
            .Select(group => group.GroupName.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            GroupFilterOptions.Add(groupName);
        }
    }

    private void ApplyFilters()
    {
        if (_isReloading)
        {
            return;
        }

        var options = CreateFilterOptions();
        _filteredHistory = _historyExport.Filter(_allHistory, _data.ActiveClassroom.Groups, options).ToList();
        History.Clear();
        foreach (var entry in _filteredHistory)
        {
            History.Add(new DrawHistoryRowViewModel(
                entry.DrawnAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                string.Join("、", entry.MemberNames)));
        }

        RebuildStatistics(_filteredHistory);
        UpdateEmptyState();
        NotifyStateChanged();
    }

    private HistoryFilterOptions CreateFilterOptions() => new(
        StartDate,
        EndDate,
        OptionFromIndex(MemberFilterOptions, SelectedMemberFilterIndex),
        OptionFromIndex(GroupFilterOptions, SelectedGroupFilterIndex));

    private static string? OptionFromIndex(IReadOnlyList<string> options, int index) =>
        index > 0 && index < options.Count ? options[index] : null;

    private void RebuildStatistics(IReadOnlyList<DrawHistoryEntry> source)
    {
        OverviewStatistics.Clear();
        MemberStatistics.Clear();
        TopMemberStatistics.Clear();
        GroupStatistics.Clear();
        DailyTrendStatistics.Clear();
        NeverDrawnMembers.Clear();
        StaleMembers.Clear();

        var memberNames = _data.ActiveClassroom.Members
            .Select(member => member.Name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var drawCounts = source
            .SelectMany(entry => entry.MemberNames)
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var totalSelected = drawCounts.Values.Sum();
        var coveredCount = drawCounts.Count;
        OverviewStatistics.Add(new OverviewStatisticRowViewModel("抽取记录", source.Count.ToString(), HasActiveFilters ? "当前筛选范围" : "全部历史"));
        OverviewStatistics.Add(new OverviewStatisticRowViewModel("总抽取人数", totalSelected.ToString(), "按成员次数统计"));
        OverviewStatistics.Add(new OverviewStatisticRowViewModel("已覆盖成员", coveredCount.ToString(), $"共 {memberNames.Count} 人"));
        OverviewStatistics.Add(new OverviewStatisticRowViewModel("未覆盖成员", Math.Max(0, memberNames.Count - coveredCount).ToString(), "尚未出现在历史中"));

        // 公平性指数：按当前筛选范围内、至少被点过一次的成员计算
        var fairness = FairnessService.ComputeFairnessIndex(memberNames
            .Where(name => drawCounts.ContainsKey(name))
            .Select(name => drawCounts[name])
            .ToList());
        FairnessIndex = fairness;
        FairnessIndexText = fairness.ToString("F0");
        FairnessDescription = FairnessService.DescribeFairnessIndex(fairness);
        OverviewStatistics.Add(new OverviewStatisticRowViewModel("公平性指数", FairnessIndexText, FairnessDescription));

        foreach (var row in memberNames
            .Select(name => new MemberStatisticRowViewModel(
                name,
                drawCounts.TryGetValue(name, out var count) ? count : 0,
                totalSelected == 0 ? 0d : count * 100d / totalSelected))
            .Where(row => row.DrawCount > 0)
            .OrderByDescending(row => row.DrawCount)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase))
        {
            MemberStatistics.Add(row);
        }
        foreach (var row in MemberStatistics.Take(8))
        {
            TopMemberStatistics.Add(row);
        }

        foreach (var name in memberNames
            .Where(name => !drawCounts.ContainsKey(name))
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            NeverDrawnMembers.Add(name);
        }

        var reminderDays = Math.Max(1, _data.State.Settings.FairnessReminderDays);
        var lastDraws = _allHistory
            .SelectMany(entry => entry.MemberNames.Select(name => (Name: name.Trim(), entry.DrawnAt)))
            .Where(item => item.Name.Length > 0)
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Max(item => item.DrawnAt), StringComparer.OrdinalIgnoreCase);
        var threshold = DateTimeOffset.Now.AddDays(-reminderDays);
        foreach (var name in memberNames.Where(name => lastDraws.TryGetValue(name, out var last) && last < threshold))
        {
            var last = lastDraws[name];
            StaleMembers.Add(new MemberRecencyStatisticRowViewModel(name, last.LocalDateTime.ToString("yyyy-MM-dd"), Math.Max(1, (int)(DateTimeOffset.Now - last).TotalDays)));
        }

        foreach (var group in _data.ActiveClassroom.Groups.Where(group => !string.IsNullOrWhiteSpace(group.GroupName)))
        {
            var memberSet = group.Members
                .Select(name => name.Trim())
                .Where(name => name.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (memberSet.Count == 0)
            {
                continue;
            }

            var count = drawCounts
                .Where(item => memberSet.Contains(item.Key))
                .Sum(item => item.Value);
            GroupStatistics.Add(new GroupStatisticRowViewModel(
                group.GroupName.Trim(), count, memberSet.Count,
                totalSelected == 0 ? 0d : count * 100d / totalSelected,
                memberSet.Count == 0 ? 0d : (double)count / memberSet.Count));
        }

        var latest = source
            .OrderByDescending(entry => entry.DrawnAt)
            .FirstOrDefault();
        TotalDrawCountText = HasActiveFilters
            ? $"{source.Count}/{_allHistory.Count}"
            : _allHistory.Count.ToString();
        DrawnMemberCountText = drawCounts.Count.ToString();
        NeverDrawnCountText = NeverDrawnMembers.Count.ToString();
        LatestDrawText = latest is null
            ? "暂无"
            : latest.DrawnAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        OverviewStatistics.Add(new OverviewStatisticRowViewModel("最近一次抽取", LatestDrawText, "按本地时间显示"));

        var today = DateTimeOffset.Now.LocalDateTime.Date;
        var firstDay = today.AddDays(-29);
        var countsByDate = source
            .SelectMany(entry => entry.MemberNames.Select(_ => entry.DrawnAt.LocalDateTime.Date))
            .GroupBy(date => date)
            .ToDictionary(group => group.Key, group => group.Count());
        var maxDaily = Math.Max(1, countsByDate.Values.DefaultIfEmpty(0).Max());
        for (var day = firstDay; day <= today; day = day.AddDays(1))
        {
            var count = countsByDate.TryGetValue(day, out var dailyCount) ? dailyCount : 0;
            DailyTrendStatistics.Add(new DailyTrendStatisticRowViewModel(
                day.ToString("MM-dd"), count, count * 100d / Math.Max(1, totalSelected), Math.Max(2d, count * 100d / maxDaily)));
        }

        OnPropertyChanged(nameof(HasMemberStatistics));
        OnPropertyChanged(nameof(HasGroupStatistics));
        OnPropertyChanged(nameof(HasStatistics));
        OnPropertyChanged(nameof(HasTrendData));
        OnPropertyChanged(nameof(HasNeverDrawnMembers));
        OnPropertyChanged(nameof(HasStaleMembers));
        OnPropertyChanged(nameof(MemberStatisticsVisibility));
        OnPropertyChanged(nameof(GroupStatisticsVisibility));
        OnPropertyChanged(nameof(NeverDrawnMembersVisibility));
        OnPropertyChanged(nameof(StaleMembersVisibility));
    }

    private void UpdateEmptyState()
    {
        if (!HasAnyHistory)
        {
            EmptyStateTitle = "暂无抽取历史";
            EmptyStateDescription = "完成一次点名后，这里会显示记录和统计。";
            return;
        }

        EmptyStateTitle = "没有匹配的历史记录";
        EmptyStateDescription = "调整日期、成员或小组筛选后再查看。";
    }

    [RelayCommand(CanExecute = nameof(HasAnyHistory))]
    private async Task ClearHistoryAsync()
    {
        if (!await _dialogs.ConfirmAsync("清除抽取历史", "确定要永久清除全部抽取历史吗？", "清除"))
        {
            return;
        }

        _data.ActiveClassroom.DrawHistory.Clear();
        await ReloadAsync();
        await _data.SaveAsync();
        StatusSeverity = InfoBarSeverity.Success;
        StatusMessage = "抽取历史已清除";
    }

    [RelayCommand(CanExecute = nameof(HasActiveFilters))]
    private void ClearFilters()
    {
        StartDate = null;
        EndDate = null;
        SelectedMemberFilterIndex = 0;
        SelectedGroupFilterIndex = 0;
        ApplyFilters();
    }

    [RelayCommand(CanExecute = nameof(HasFilteredHistory))]
    private async Task ExportCsvAsync()
    {
        await _files.SaveTextAsync("PickWeight-历史", ".csv", _historyExport.ExportCsv(_filteredHistory, _data.ActiveClassroom.Groups));
        StatusSeverity = InfoBarSeverity.Success;
        StatusMessage = $"已导出 {_filteredHistory.Count} 条历史记录。";
    }

    [RelayCommand(CanExecute = nameof(HasFilteredHistory))]
    private async Task ExportJsonAsync()
    {
        await _files.SaveTextAsync("PickWeight-历史", ".json", _historyExport.ExportJson(_filteredHistory, _data.ActiveClassroom.Groups, CreateFilterOptions(), DateTimeOffset.Now));
        StatusSeverity = InfoBarSeverity.Success;
        StatusMessage = $"已导出 {_filteredHistory.Count} 条历史记录。";
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(HasAnyHistory));
        OnPropertyChanged(nameof(HasFilteredHistory));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(HistoryListVisibility));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(ClearEmptyFiltersVisibility));
        ClearHistoryCommand.NotifyCanExecuteChanged();
        ClearFiltersCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
        ExportJsonCommand.NotifyCanExecuteChanged();
    }
}

public sealed record DrawHistoryRowViewModel(string DrawnAt, string MemberNames);

public sealed record OverviewStatisticRowViewModel(string Title, string Value, string Description);

public sealed record MemberStatisticRowViewModel(string Name, int DrawCount, double Percentage)
{
    public string PercentageText => $"{Percentage:F1}%";
}

public sealed record GroupStatisticRowViewModel(string GroupName, int DrawCount, int MemberCount, double Percentage, double AverageDrawCount)
{
    public string PercentageText => $"{Percentage:F1}%";
    public string AverageDrawCountText => AverageDrawCount.ToString("F1");
}

public sealed record DailyTrendStatisticRowViewModel(string Date, int DrawCount, double Percentage, double BarWidth);

public sealed record MemberRecencyStatisticRowViewModel(string Name, string LastDrawnAt, int DaysSinceLastDraw)
{
    public string Detail => $"上次被点：{LastDrawnAt}，已间隔 {DaysSinceLastDraw} 天";
}

