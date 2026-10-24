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

/// <summary>
/// Keeps the member page deliberately lightweight: the list exposes recognition
/// and availability, while all detail edits happen in a modal editor.
/// </summary>
public partial class MembersPageViewModel : ObservableObject, IEditablePageViewModel
{
    private const int LoadBatchSize = 100;

    private readonly AppDataService _data;
    private readonly FileDialogService _files;
    private readonly DialogService _dialogs;
    private readonly IMemberImportExportService _importExport;
    private readonly IMemberSpreadsheetImportService _spreadsheetImport;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private bool _hasLoaded;
    private bool _isLoading;
    private bool _suppressAutoSave;
    private readonly Stack<List<Member>> _undo = [];
    private readonly Stack<List<Member>> _redo = [];

    public ObservableCollection<MemberRowViewModel> Members { get; } = [];
    public ObservableCollection<MemberRowViewModel> FilteredMembers { get; } = [];

    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private InfoBarSeverity statusSeverity = InfoBarSeverity.Success;
    [ObservableProperty] private MemberRowViewModel? selectedMember;
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private int sortOptionIndex;

    /// <summary>排序选项：0 姓名，1 学号，2 座位，3 被点次数，4 权重，5 最近被点。</summary>
    public static IReadOnlyList<string> SortOptions { get; } = ["姓名", "学号", "座位", "被点次数", "权重", "最近被点"];
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool HasMembers => Members.Count > 0;
    public bool HasFilteredMembers => FilteredMembers.Count > 0;
    public Visibility MembersListVisibility => HasFilteredMembers ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyStateVisibility => HasFilteredMembers ? Visibility.Collapsed : Visibility.Visible;
    public string EmptyStateTitle => HasMembers ? "没有匹配成员" : "还没有成员";
    public string EmptyStateDescription => HasMembers
        ? "调整搜索关键字后再查看。"
        : "点击“新增成员”建立第一位成员，或从 TXT 文件导入名单。";

    public MembersPageViewModel(
        AppDataService data,
        FileDialogService files,
        DialogService dialogs,
        IMemberImportExportService importExport,
        IMemberSpreadsheetImportService spreadsheetImport)
    {
        _data = data;
        _files = files;
        _dialogs = dialogs;
        _importExport = importExport;
        _spreadsheetImport = spreadsheetImport;
    }

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));
    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSortOptionIndexChanged(int value) => ApplyFilter();

    public void Reload()
    {
        _hasLoaded = false;
        Members.Clear();
        foreach (var member in _data.ActiveClassroom.Members)
        {
            Members.Add(CreateRow(member.Clone()));
        }

        ApplyFilter();
        IsDirty = false;
        _hasLoaded = true;
    }

    public async Task EnsureLoadedAsync()
    {
        if (_hasLoaded || _isLoading)
        {
            return;
        }

        _isLoading = true;
        try
        {
            StatusSeverity = InfoBarSeverity.Informational;
            StatusMessage = "正在加载成员...";
            Members.Clear();
            FilteredMembers.Clear();

            var source = _data.ActiveClassroom.Members.Select(member => member.Clone()).ToList();
            for (var index = 0; index < source.Count; index++)
            {
                Members.Add(CreateRow(source[index]));
                if ((index + 1) % LoadBatchSize == 0)
                {
                    StatusMessage = $"正在加载成员 {index + 1}/{source.Count}...";
                    await Task.Yield();
                }
            }

            await ApplyFilterAsync();
            IsDirty = false;
            StatusSeverity = InfoBarSeverity.Success;
            StatusMessage = source.Count == 0 ? string.Empty : $"已加载 {source.Count} 名成员。";
            _hasLoaded = true;
        }
        finally
        {
            // 加载失败也必须复位，否则本页此后所有 Loaded 都会直接跳过，列表永久空白
            _isLoading = false;
        }
    }

    public bool ValidateStudentId(string? studentId, out string error)
    {
        var value = studentId?.Trim() ?? string.Empty;
        if (value.Length > 50)
        {
            error = "学号不能超过 50 个字符。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public bool TryValidateMemberName(
        MemberRowViewModel? editingMember,
        string? proposedName,
        out string error,
        string? proposedStudentId = null)
    {
        var name = proposedName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            error = "成员姓名不能为空。";
            return false;
        }

        if (!ValidateStudentId(proposedStudentId, out error))
        {
            return false;
        }

        if (Members.Any(member => !ReferenceEquals(member, editingMember)
            && string.Equals(member.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"成员“{name}”已存在。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public async Task<bool> AddMemberAsync(
        string name,
        MemberGender? gender,
        double explicitWeight,
        int calledCount,
        string? studentId = null,
        string? note = null,
        bool isAbsentToday = false)
    {
        if (!TryValidateMemberName(null, name, out var error, studentId))
        {
            StatusSeverity = InfoBarSeverity.Error;
            StatusMessage = error;
            return false;
        }

        var model = new Member();
        model.Name = name.Trim();
        model.StudentId = NormalizeOptionalText(studentId);
        model.Note = NormalizeOptionalText(note);
        model.AbsentOn = isAbsentToday ? DateOnly.FromDateTime(DateTime.Today) : null;
        model.Gender = gender;
        MemberRules.ChangeCalledCount(model, Math.Max(0, calledCount));
        MemberRules.SetExplicitWeight(model, NormalizeWeight(explicitWeight));

        RecordUndoSnapshot();
        _suppressAutoSave = true;
        try
        {
            Members.Add(CreateRow(model));
            ApplyFilter();
            IsDirty = true;
        }
        finally
        {
            _suppressAutoSave = false;
        }

        return await SaveMembersAsync("成员已添加并保存。");
    }

    public async Task<bool> UpdateMemberAsync(
        MemberRowViewModel member,
        string name,
        MemberGender? gender,
        double explicitWeight,
        int calledCount,
        string? studentId = null,
        string? note = null,
        bool isAbsentToday = false)
    {
        if (!TryValidateMemberName(member, name, out var error, studentId))
        {
            StatusSeverity = InfoBarSeverity.Error;
            StatusMessage = error;
            return false;
        }

        RecordUndoSnapshot();
        _suppressAutoSave = true;
        try
        {
            member.ApplyEdit(name, gender, explicitWeight, calledCount, studentId, note, isAbsentToday);
            ApplyFilter();
            IsDirty = true;
        }
        finally
        {
            _suppressAutoSave = false;
        }

        return await SaveMembersAsync("成员资料已保存。");
    }

    public async Task DeleteRowsAsync(IEnumerable<object> selectedItems)
    {
        var selected = ExtractSelectedRows(selectedItems);
        if (selected.Count == 0 && SelectedMember is not null)
        {
            selected.Add(SelectedMember);
        }

        if (selected.Count == 0)
        {
            StatusSeverity = InfoBarSeverity.Informational;
            StatusMessage = "请先选择要删除的成员。";
            return;
        }

        RecordUndoSnapshot();
        _suppressAutoSave = true;
        try
        {
            foreach (var member in selected)
            {
                Members.Remove(member);
            }

            SelectedMember = null;
            ApplyFilter();
            IsDirty = true;
        }
        finally
        {
            _suppressAutoSave = false;
        }

        await SaveMembersAsync($"已删除 {selected.Count} 名成员并保存。");
    }

    public async Task SetRowsActiveAsync(IEnumerable<object> selectedItems, bool isActive)
    {
        var selected = ExtractSelectedRows(selectedItems);
        if (selected.Count == 0 && SelectedMember is not null)
        {
            selected.Add(SelectedMember);
        }

        if (selected.Count == 0)
        {
            StatusSeverity = InfoBarSeverity.Informational;
            StatusMessage = "请先选择要批量修改的成员。";
            return;
        }

        RecordUndoSnapshot();
        _suppressAutoSave = true;
        try
        {
            foreach (var member in selected)
            {
                member.IsActive = isActive;
            }

            ApplyFilter();
            IsDirty = true;
        }
        finally
        {
            _suppressAutoSave = false;
        }

        await SaveMembersAsync($"已{(isActive ? "启用" : "停用")} {selected.Count} 名成员并保存。");
    }

    public Task SaveAsync() => SaveMembersAsync("成员数据已保存。");

    public async Task SetRowsWeightAsync(IEnumerable<object> selectedItems, double weight)
    {
        var selected = ExtractSelectedRows(selectedItems);
        if (selected.Count == 0 && SelectedMember is not null) selected.Add(SelectedMember);
        if (selected.Count == 0)
        {
            StatusSeverity = InfoBarSeverity.Informational;
            StatusMessage = "请先选择要设置权重的成员。";
            return;
        }

        RecordUndoSnapshot();
        _suppressAutoSave = true;
        try
        {
            foreach (var member in selected) member.SetWeight(NormalizeWeight(weight));
            IsDirty = true;
        }
        finally { _suppressAutoSave = false; }
        await SaveMembersAsync($"已设置 {selected.Count} 名成员的权重。");
    }

    public async Task UndoAsync()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Members.Select(member => member.ToModel()).ToList());
        await RestoreSnapshotAsync(_undo.Pop(), "已撤销上一次成员编辑。");
    }

    public async Task RedoAsync()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Members.Select(member => member.ToModel()).ToList());
        await RestoreSnapshotAsync(_redo.Pop(), "已恢复下一次成员编辑。");
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var content = await _files.OpenTextAsync(".txt");
        if (content is null)
        {
            StatusSeverity = InfoBarSeverity.Informational;
            StatusMessage = "已取消导入。";
            return;
        }

        var models = Members.Select(member => member.ToModel()).ToList();
        var preview = _importExport.PreviewImport(content, models);
        if (preview.AddCount == 0)
        {
            StatusSeverity = InfoBarSeverity.Informational;
            StatusMessage = $"没有可导入的新成员，跳过 {preview.SkipCount} 行。";
            return;
        }

        if (!await _dialogs.ConfirmContentAsync("导入预览", CreateImportPreviewContent(preview), "导入有效行"))
        {
            StatusSeverity = InfoBarSeverity.Informational;
            StatusMessage = "已取消导入。";
            return;
        }

        var result = _importExport.ApplyImport(preview, models);
        RecordUndoSnapshot();
        _suppressAutoSave = true;
        try
        {
            ReplaceRows(models);
            IsDirty = true;
        }
        finally
        {
            _suppressAutoSave = false;
        }

        var saved = await SaveMembersAsync($"已新增 {result.AddedCount} 名成员并保存。");
        if (saved && preview.HasErrors)
        {
            StatusSeverity = InfoBarSeverity.Warning;
            StatusMessage = $"已新增 {result.AddedCount} 名成员；跳过 {result.SkippedCount} 行。";
        }
    }

    /// <summary>粘贴导入前预览：先剥离行首编号并归一化，再复用导入服务的预览逻辑。</summary>
    public MemberImportPreview PreviewImport(string pastedText) =>
        _importExport.PreviewImport(
            FairnessService.NormalizePastedLines(pastedText),
            Members.Select(member => member.ToModel()));

    /// <summary>粘贴导入：归一化文本后按导入流程新增成员并保存。</summary>
    public async Task PasteImportAsync(string pastedText)
    {
        var normalized = FairnessService.NormalizePastedLines(pastedText);
        var models = Members.Select(member => member.ToModel()).ToList();
        var preview = _importExport.PreviewImport(normalized, models);
        if (preview.AddCount == 0)
        {
            StatusSeverity = InfoBarSeverity.Informational;
            StatusMessage = $"没有可导入的新成员，跳过 {preview.SkipCount} 行。";
            return;
        }

        var result = _importExport.ApplyImport(preview, models);
        RecordUndoSnapshot();
        _suppressAutoSave = true;
        try
        {
            ReplaceRows(models);
            IsDirty = true;
        }
        finally
        {
            _suppressAutoSave = false;
        }

        var saved = await SaveMembersAsync($"已新增 {result.AddedCount} 名成员并保存。");
        if (saved && preview.HasErrors)
        {
            StatusSeverity = InfoBarSeverity.Warning;
            StatusMessage = $"已新增 {result.AddedCount} 名成员；跳过 {result.SkippedCount} 行。";
        }
    }

    [RelayCommand]
    private async Task ImportExcelAsync()    {
        var path = await _files.OpenPathAsync(".xlsx");
        if (path is null) return;
        try
        {
            var content = await _spreadsheetImport.ReadAsMemberTextAsync(path);
            var models = Members.Select(member => member.ToModel()).ToList();
            var preview = _importExport.PreviewImport(content, models);
            if (preview.AddCount == 0 || !await _dialogs.ConfirmContentAsync("Excel 导入预览", CreateImportPreviewContent(preview), "导入有效行")) return;
            var result = _importExport.ApplyImport(preview, models);
            RecordUndoSnapshot();
            _suppressAutoSave = true;
            try { ReplaceRows(models); IsDirty = true; }
            finally { _suppressAutoSave = false; }
            await SaveMembersAsync($"已从 Excel 新增 {result.AddedCount} 名成员并保存。");
        }
        catch (Exception exception)
        {
            StatusSeverity = InfoBarSeverity.Error;
            StatusMessage = $"Excel 导入失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        await _files.SaveTextAsync("PickWeight-成员", ".txt", _importExport.Export(Members.Select(member => member.ToModel())));
        StatusSeverity = InfoBarSeverity.Success;
        StatusMessage = "导出操作已完成。";
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        if (!await _dialogs.ConfirmAsync("重置成员数据", "确定要重置所有成员的显式权重和被点次数吗？", "重置"))
        {
            return;
        }

        var reset = Members.Select(row => row.ToModel()).ToList();
        foreach (var member in reset)
        {
            MemberRules.Reset(member);
        }

        RecordUndoSnapshot();
        _suppressAutoSave = true;
        try
        {
            ReplaceRows(reset);
            IsDirty = true;
        }
        finally
        {
            _suppressAutoSave = false;
        }

        await SaveMembersAsync("成员权重和被点次数已重置并保存。");
    }

    private async Task<bool> SaveMembersAsync(string successMessage)
    {
        await _saveGate.WaitAsync();
        try
        {
            var models = Members.Select(member => member.ToModel()).ToList();
            var invalidMessage = ValidateMembers(models);
            if (invalidMessage is not null)
            {
                StatusSeverity = InfoBarSeverity.Error;
                StatusMessage = invalidMessage;
                return false;
            }

            // 同一行在会话内被多次改名时以最新目标为准；ToDictionary 遇重复键会直接抛异常
            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in Members
                .Where(member => !string.Equals(member.OriginalName.Trim(), member.Name.Trim(), StringComparison.Ordinal)))
            {
                renames[member.OriginalName.Trim()] = member.Name.Trim();
            }

            await _data.ReplaceMembersAsync(models, renames);
            foreach (var member in Members)
            {
                member.AcceptChanges();
            }

            IsDirty = false;
            StatusSeverity = InfoBarSeverity.Success;
            StatusMessage = successMessage;
            return true;
        }
        catch (Exception ex)
        {
            IsDirty = true;
            StatusSeverity = InfoBarSeverity.Error;
            StatusMessage = $"成员数据保存失败：{ex.Message}";
            return false;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void RecordUndoSnapshot()
    {
        _undo.Push(Members.Select(member => member.ToModel()).ToList());
        _redo.Clear();
    }

    private async Task RestoreSnapshotAsync(IReadOnlyList<Member> snapshot, string successMessage)
    {
        _suppressAutoSave = true;
        try
        {
            ReplaceRows(snapshot.Select(member => member.Clone()));
            IsDirty = true;
        }
        finally { _suppressAutoSave = false; }
        await SaveMembersAsync(successMessage);
    }

    private void OnRowChanged()
    {
        IsDirty = true;
        if (!_suppressAutoSave)
        {
            _ = SaveMembersAsync("成员状态已自动保存。");
        }
    }

    private MemberRowViewModel CreateRow(Member member)
    {
        var row = new MemberRowViewModel(member, OnRowChanged);
        var seat = SeatMapService.FindSeat(_data.ActiveClassroom.SeatMap ?? new SeatMap(), member.Name);
        row.UpdateSeat(seat);
        var lastDrawn = _data.ActiveClassroom.DrawHistory
            .Where(entry => entry.MemberNames.Contains(member.Name, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.DrawnAt)
            .FirstOrDefault()?.DrawnAt;
        row.UpdateLastDrawnAt(lastDrawn);
        return row;
    }

    private void ReplaceRows(IEnumerable<Member> models)
    {
        Members.Clear();
        foreach (var member in models)
        {
            Members.Add(CreateRow(member));
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        RefreshHeatBars();
        var query = SearchText.Trim();
        FilteredMembers.Clear();
        foreach (var member in SortMembers(
            Members.Where(member => query.Length == 0 || member.Name.Contains(query, StringComparison.OrdinalIgnoreCase))))
        {
            FilteredMembers.Add(member);
        }

        NotifyCollectionStateChanged();
    }

    private async Task ApplyFilterAsync()
    {
        RefreshHeatBars();
        var query = SearchText.Trim();
        var filtered = SortMembers(
            Members.Where(member => query.Length == 0 || member.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        FilteredMembers.Clear();
        for (var index = 0; index < filtered.Count; index++)
        {
            FilteredMembers.Add(filtered[index]);
            if ((index + 1) % LoadBatchSize == 0)
            {
                await Task.Yield();
            }
        }

        NotifyCollectionStateChanged();
    }

    private void RefreshHeatBars()
    {
        var maxCalledCount = Members.Count == 0 ? 0 : Members.Max(member => member.CalledCount);
        foreach (var member in Members)
        {
            member.UpdateHeatBar(maxCalledCount);
        }
    }

        /// <summary>按当前排序选项排序；学号/座位/最近被点为空的成员始终排到最后。</summary>
    private IEnumerable<MemberRowViewModel> SortMembers(IEnumerable<MemberRowViewModel> members)
    {
        return SortOptionIndex switch
        {
            1 => members
                .OrderByDescending(member => string.IsNullOrWhiteSpace(member.StudentId))
                .ThenBy(member => member.StudentId?.Trim(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(member => member.Name, StringComparer.OrdinalIgnoreCase),
            2 => members
                .OrderByDescending(member => !member.IsSeated)
                .ThenBy(member => member.SeatTuple.Column)
                .ThenBy(member => member.SeatTuple.Row)
                .ThenBy(member => member.Name, StringComparer.OrdinalIgnoreCase),
            3 => members
                .OrderByDescending(member => member.CalledCount)
                .ThenBy(member => member.Name, StringComparer.OrdinalIgnoreCase),
            4 => members
                .OrderBy(member => member.Weight)
                .ThenBy(member => member.Name, StringComparer.OrdinalIgnoreCase),
            5 => members
                .OrderByDescending(member => !member.LastDrawnAt.HasValue)
                .ThenByDescending(member => member.LastDrawnAt)
                .ThenBy(member => member.Name, StringComparer.OrdinalIgnoreCase),
            _ => members.OrderBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static object CreateImportPreviewContent(MemberImportPreview preview)
    {
        var panel = new StackPanel { Spacing = 10, MaxWidth = 620 };
        panel.Children.Add(new TextBlock
        {
            Text = $"将新增 {preview.AddCount} 人，跳过 {preview.SkipCount} 行。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = "确认后只导入有效新增行；重复、空行和无效权重不会写入成员名单。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new ListView
        {
            MaxHeight = 320,
            ItemsSource = preview.Items
                .Take(80)
                .Select(item => $"第 {item.LineNumber} 行：{DescribeImportAction(item)}")
                .ToList()
        });
        return panel;
    }

    private static string DescribeImportAction(MemberImportPreviewItem item) =>
        item.Action == MemberImportAction.Add
            ? $"新增 {item.Name}（权重 {item.ExplicitWeight:G6}）"
            : $"{item.Reason}{(string.IsNullOrWhiteSpace(item.Name) ? string.Empty : $"：{item.Name}")}";

    private void NotifyCollectionStateChanged()
    {
        OnPropertyChanged(nameof(HasMembers));
        OnPropertyChanged(nameof(HasFilteredMembers));
        OnPropertyChanged(nameof(MembersListVisibility));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateDescription));
    }

    private static List<MemberRowViewModel> ExtractSelectedRows(object? selectedItems)
    {
        if (selectedItems is not System.Collections.IEnumerable items)
        {
            return [];
        }

        return items.OfType<MemberRowViewModel>().Distinct().ToList();
    }

    private static string? ValidateMembers(IReadOnlyList<Member> members)
    {
        if (members.Any(member => string.IsNullOrWhiteSpace(member.Name)))
        {
            return "成员姓名不能为空，请修改后再保存。";
        }

        var duplicate = members
            .GroupBy(member => member.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        return duplicate is null ? null : $"成员姓名“{duplicate.Key}”重复，请修改后再保存。";
    }

    private static double NormalizeWeight(double value) => Math.Clamp(
        double.IsFinite(value) ? value : 0d,
        0d,
        MemberRules.MaximumWeight);

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}





