using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using PickWeight.Core.Abstractions;
using PickWeight.Core.Models;
using PickWeight.Core.Services;
using PickWeight_App.Services;

namespace PickWeight_App.ViewModels;

/// <summary>
/// Presentation state for the primary draw page.  Rule edits are deliberately
/// staged in <see cref="RuleDialogDraft"/> so Cancel never changes the current
/// draw configuration.
/// </summary>
public partial class DrawPageViewModel : ObservableObject
{
    private readonly AppDataService _data;
    private readonly IDrawService _drawService;
    private readonly IDrawExecutionService _execution;
    private readonly IDrawExecutionCoordinator _drawCoordinator;
    private readonly ISettingsService _settings;
    private int _previewRefreshVersion;
    private bool _synchronizingGroups;
    private PreparedDraw? _currentDraw;
    private DrawExecutionLease? _drawLease;
    private readonly object _drawOperationGate = new();
    private Task? _activeDrawTask;
    private CancellationTokenSource? _activeDrawCancellation;
    private bool _navigationRequested;
    private readonly EventHandler<SettingsChangedEventArgs> _settingsChangedHandler;
    private readonly EventHandler _groupsChangedHandler;
    private readonly EventHandler _membersChangedHandler;
    private readonly EventHandler _activeClassroomChangedHandler;

    /// <summary>当前提醒的长期未点成员，抽取前会临时提升 3 倍权重。</summary>
    private readonly HashSet<string> _boostedNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本次会话已忽略的成员姓名，不再重复提醒。</summary>
    private readonly HashSet<string> _ignoredNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>临时提升权重前的原始值，用于抽取/预览后恢复。</summary>
    private readonly Dictionary<string, double> _boostedOriginalWeights = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<DrawResultItemViewModel> Results { get; } = [];
    public ObservableCollection<DrawProbabilityPreviewRowViewModel> ProbabilityPreview { get; } = [];
    public ObservableCollection<RuleSummaryRowViewModel> RuleSummaryRows { get; } = [];
    public ObservableCollection<string> GroupFilterOptions { get; } = [];
    public ObservableCollection<DrawPreset> DrawPresets { get; } = [];
    public ObservableCollection<AttendanceMemberViewModel> AttendanceMembers { get; } = [];

    public event Action? DrawStarted;
    public event Action? DrawCompleted;

    [ObservableProperty]
    private int absentCount;

    [ObservableProperty]
    private int presentCount;

    public string AttendanceSummary => $"今日考勤 · 在场 {PresentCount} 人 · 请假 {AbsentCount} 人";

    public bool HasAttendanceMembers => AttendanceMembers.Count > 0;
    public bool CanMarkAllPresent => AbsentCount > 0;

    [ObservableProperty]
    private int selectedDrawModeIndex;

    [ObservableProperty]
    private double targetCount = 1d;

    [ObservableProperty]
    private int includeGroupFilterIndex;

    [ObservableProperty]
    private int excludeGroupFilterIndex;

    [ObservableProperty]
    private double excludeRecentDrawCount;

    [ObservableProperty]
    private bool onlyNeverDrawn;

    [ObservableProperty]
    private string statusMessage = "准备开始抽取";

    [ObservableProperty]
    private string candidateSummary = "候选人：0";

    [ObservableProperty]
    private RuleDialogDraft? ruleDraft;

    [ObservableProperty]
    private DrawPreset? selectedDrawPreset;

    [ObservableProperty]
    private int currentRoundNumber = 1;

    [ObservableProperty]
    private int roundDrawnCount;

    [ObservableProperty]
    private int roundDrawableCount;

    [ObservableProperty]
    private bool roundModeEnabled;

    public string RoundProgressText => RoundModeEnabled
        ? $"第 {CurrentRoundNumber} 轮 · 已点 {RoundDrawnCount}/{RoundDrawnCount + RoundDrawableCount}"
        : string.Empty;

    public Visibility RoundProgressVisibility => RoundModeEnabled ? Visibility.Visible : Visibility.Collapsed;

    public double RoundProgressValue =>
        RoundDrawnCount + RoundDrawableCount > 0
            ? RoundDrawnCount * 100d / (RoundDrawnCount + RoundDrawableCount)
            : 0d;

    public bool CanStartNextRound => RoundDrawnCount > 0;

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool CanDiscardCurrentDraw => _currentDraw is not null && Results.Count > 0;
    public bool HasSelectedResults => Results.Any(row => !row.IsDiscarded);
    public bool HasProbabilityPreview => ProbabilityPreview.Count > 0;
    public Visibility ProbabilityPreviewVisibility => HasProbabilityPreview ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyProbabilityPreviewVisibility => HasProbabilityPreview ? Visibility.Collapsed : Visibility.Visible;
    public string ProbabilityPreviewEmptyText => "当前规则没有可抽取候选人。";
    public bool CanClearDrawFilters =>
        IncludeGroupFilterIndex > 0 ||
        ExcludeGroupFilterIndex > 0 ||
        ExcludeRecentDrawCount >= 1d ||
        OnlyNeverDrawn;

    /// <summary>公平性提醒：长期未被点到的成员姓名，空则隐藏提醒栏。</summary>
    public IReadOnlyList<string> LongUncalledNames => _longUncalledNames;

    /// <summary>本次会话已忽略的长期未点成员姓名，用于关闭提醒后不再弹出。</summary>
    public IReadOnlySet<string> IgnoredUncalledNames => _ignoredNames;

    private readonly List<string> _longUncalledNames = [];

    public bool HasFairnessReminder => LongUncalledNames.Count > 0;

    public string FairnessReminderText =>
        LongUncalledNames.Count == 0
            ? string.Empty
            : $"{string.Join("、", LongUncalledNames)} 已 {_settings.Current.FairnessReminderDays} 天内未被点到";

    public DrawPageViewModel(
        AppDataService data,
        IDrawService drawService,
        IDrawExecutionService execution,
        ISettingsService settings)
        : this(data, drawService, execution, settings, new DrawExecutionCoordinator())
    {
    }

    public DrawPageViewModel(
        AppDataService data,
        IDrawService drawService,
        IDrawExecutionService execution,
        ISettingsService settings,
        IDrawExecutionCoordinator drawCoordinator)
    {
        _data = data;
        _drawService = drawService;
        _execution = execution;
        _drawCoordinator = drawCoordinator;
        _settings = settings;

        RebuildGroupFilterOptions();
        RebuildPresets();
        LoadRulesFromSettings();

        _settingsChangedHandler = (_, args) =>
        {
            if (args.DrawRulesChanged)
            {
                LoadRulesFromSettings();
                _ = RefreshProbabilityPreviewAsync();
            }
            if (args.FairnessReminderDaysChanged)
            {
                RefreshFairnessReminder();
            }
        };
        _groupsChangedHandler = (_, _) => _ = HandleGroupsChangedAsync();
        _membersChangedHandler = (_, _) =>
        {
            _ = RefreshProbabilityPreviewAsync();
            RefreshAttendance();
            RefreshFairnessReminder();
        };
        _activeClassroomChangedHandler = (_, _) => OnPropertyChanged(nameof(HasSeatMapVisibility));

        _settings.Changed += _settingsChangedHandler;
        _data.GroupsChanged += _groupsChangedHandler;
        _data.MembersChanged += _membersChangedHandler;
        _data.ActiveClassroomChanged += _activeClassroomChangedHandler;

        _ = RefreshProbabilityPreviewAsync();
        RefreshFairnessReminder();
        OnPropertyChanged(nameof(HasSeatMapVisibility));
    }

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));
    partial void OnTargetCountChanged(double value) => RefreshRuleSummary();
    partial void OnIncludeGroupFilterIndexChanged(int value) => RefreshFilters();
    partial void OnExcludeGroupFilterIndexChanged(int value) => RefreshFilters();
    partial void OnExcludeRecentDrawCountChanged(double value) => RefreshFilters();
    partial void OnOnlyNeverDrawnChanged(bool value) => RefreshFilters();

    /// <summary>
    /// 主页键盘 1-4 切换点名模式：直接写入设置使其对下一次抽取立即生效。
    /// 之前仅改 SelectedDrawModeIndex，该属性不参与抽取，导致快捷键静默失效。
    /// </summary>
    public async Task ApplyDrawModeShortcutAsync(int modeIndex)
    {
        var mode = (DrawMode)Math.Clamp(modeIndex, 0, 3);
        if (_settings.Current.DrawMode == mode)
        {
            return;
        }

        await _settings.UpdateAsync(settings => settings.DrawMode = mode);
        LoadRulesFromSettings();
        StatusMessage = $"已切换到{ModeText(mode)}。";
        _ = RefreshProbabilityPreviewAsync();
    }

    [RelayCommand]
    private async Task DrawAsync()
    {
        if (!TryStartActiveOperation(
                out var completion,
                out var operationCancellation))
        {
            StatusMessage = "已有抽取正在处理，请稍后再试。";
            return;
        }

        Exception? operationError = null;
        try
        {
            await DrawCoreAsync(operationCancellation.Token);
        }
        catch (Exception exception)
        {
            operationError = exception;
            throw;
        }
        finally
        {
            FinishActiveOperation(completion, operationCancellation, operationError);
        }
    }

    private async Task DrawCoreAsync(CancellationToken operationToken)
    {
        var drawLease = _drawCoordinator.TryAcquire();
        if (drawLease is null)
        {
            StatusMessage = "已有抽取正在处理，请稍后再试。";
            return;
        }

        // 本次优先：抽取前给被提醒成员临时提升 3 倍权重，抽取结束后恢复原值
        var boostApplied = ApplyBoostedWeights();
        PreparedDraw prepared;
        try
        {
            prepared = await _execution.PrepareAsync(new DrawExecutionRequest(
                NormalizeTargetCount(),
                CurrentFilterOptions(),
                _settings.Current.DrawMode),
                operationToken);
            operationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            drawLease.Dispose();
            throw;
        }
        finally
        {
            // 预览与抽取共用同一线程，必须先恢复权重再走后续流程
            if (boostApplied)
            {
                RestoreBoostedWeights();
            }
        }

        if (boostApplied)
        {
            // PrepareAsync 在提权窗口内捕获的快照带有临时权重，这里改回真实值，
            // 避免后续丢弃/重建把提权后的权重写回成员并持久化。
            CorrectBoostedSnapshots(prepared);
        }

        if (prepared.SelectedMembers.Count == 0)
        {
            drawLease.Dispose();
            Results.Clear();
            _currentDraw = null;
            NotifyDrawStateChanged();
            StatusMessage = prepared.Result.Message ?? "当前规则没有可抽取候选人。";
            await RefreshProbabilityPreviewAsync(reloadWeights: false, cancellationToken: operationToken);
            return;
        }

        _drawLease = drawLease;
        drawLease = null;
        var isManualSelection = prepared.DrawMode == DrawMode.Selection;
        var weights = prepared.Result.SelectedWeights ?? CreateWeightRows(prepared.SelectedMembers);
        var commitCompleted = false;
        var keepLease = false;
        try
        {
            DrawStarted?.Invoke();
            Results.Clear();
            _currentDraw = prepared;
            for (var index = 0; index < prepared.SelectedMembers.Count; index++)
            {
                var member = prepared.SelectedMembers[index];
                var weightsForMember = index < weights.Count
                    ? weights[index]
                    : new DrawnMemberWeight(member.Name, member.ExplicitWeight, MemberRules.CalledWeight(member.CalledCount));
                Results.Add(new DrawResultItemViewModel(
                    member,
                    weightsForMember,
                    prepared.DrawMode,
                    isCommitted: !isManualSelection,
                    allowsManualCommit: isManualSelection,
                    seatPosition: FindSeatPosition(member),
                    CommitResultAsync,
                    DiscardResultAsync,
                    ReplaceResultAsync,
                    RateResultAsync));
            }

            DrawCompleted?.Invoke();
            if (!isManualSelection)
            {
                operationToken.ThrowIfCancellationRequested();
                var outcome = await _execution.CommitAsync(prepared, cancellationToken: operationToken);
                operationToken.ThrowIfCancellationRequested();
                commitCompleted = true;
                StatusMessage = CreateCompletionMessage(prepared.Result, outcome);
            }
            else
            {
                StatusMessage = "请选择要确认的结果；确认后会记录历史并更新被点次数。";
            }
            NotifyDrawStateChanged();
            await RefreshProbabilityPreviewAsync(reloadWeights: false, cancellationToken: operationToken);
            if (!isManualSelection)
            {
                // 非选择模式下结果已自动提交，但保留 _currentDraw 以便对结果卡片执行
                // 换一个 / 丢弃 / 评价 操作；lease 仍可在下方释放。
                NotifyDrawStateChanged();
            }
            keepLease = isManualSelection;
        }
        catch
        {
            if (!commitCompleted)
            {
                try
                {
                    await _execution.DiscardAsync(prepared, CancellationToken.None);
                }
                catch
                {
                    // Preserve the original draw failure while still releasing
                    // the cross-entry lease below.
                }
            }

            _currentDraw = null;
            Results.Clear();
            throw;
        }
        finally
        {
            if (!keepLease)
            {
                ReleaseDrawLease();
            }
        }
    }

    /// <summary>
    /// Releases an unfinished prepared draw before the page leaves the frame.
    /// Already confirmed selection results are rebuilt so leaving the page has
    /// the same semantics as discarding only the still-unconfirmed candidates.
    /// </summary>
    public async Task OnNavigatedFromAsync()
    {
        try
        {
            Task? activeDrawTask;
            CancellationTokenSource? activeDrawCancellation;
            lock (_drawOperationGate)
            {
                _navigationRequested = true;
                activeDrawTask = _activeDrawTask;
                activeDrawCancellation = _activeDrawCancellation;
            }

            var cancellationRequested = activeDrawCancellation is not null;
            try
            {
                activeDrawCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The operation may have completed and disposed its CTS between
                // taking the snapshot and requesting cancellation.
            }
            if (activeDrawTask is not null)
            {
                try
                {
                    await activeDrawTask;
                }
                catch (OperationCanceledException) when (cancellationRequested)
                {
                    // Navigation requested the cancellation; cleanup below still
                    // releases the prepared draw and its lease.
                }
            }

            var prepared = _currentDraw;
            if (prepared is null || _drawLease is null)
            {
                Results.Clear();
                _currentDraw = null;
                ReleaseDrawLease();
                NotifyDrawStateChanged();
                return;
            }

            var committed = Results
                .Where(row => row.IsCommitted && !row.IsDiscarded)
                .Select(row => row.Member)
                .ToList();
            try
            {
                await _execution.RebuildCommittedStateAsync(prepared, committed);
            }
            finally
            {
                Results.Clear();
                _currentDraw = null;
                ReleaseDrawLease();
                NotifyDrawStateChanged();
            }
        }
        finally
        {
            UnsubscribeSingletonEvents();
        }
    }

    private void UnsubscribeSingletonEvents()
    {
        _settings.Changed -= _settingsChangedHandler;
        _data.GroupsChanged -= _groupsChangedHandler;
        _data.MembersChanged -= _membersChangedHandler;
        _data.ActiveClassroomChanged -= _activeClassroomChangedHandler;
    }

    public void BeginRuleEdit()
    {
        RuleDraft = new RuleDialogDraft(
            this,
            SelectedDrawModeIndex,
            TargetCount,
            IncludeGroupFilterIndex,
            ExcludeGroupFilterIndex,
            ExcludeRecentDrawCount,
            OnlyNeverDrawn,
            _settings.Current.DrawNoRepeatInRound,
            (int)_settings.Current.DrawSeatRegion,
            _settings.Current.DrawSeatColumn,
            _settings.Current.DrawSeatRow,
            GroupFilterOptions);
        _ = RefreshRuleDraftAsync(RuleDraft);
    }

    public async Task ApplyRuleDraftAsync()
    {
        if (RuleDraft is not { } draft)
        {
            return;
        }

        var mode = (DrawMode)Math.Clamp(draft.SelectedDrawModeIndex, 0, 3);
        var targetCount = NormalizeTargetCount(draft.TargetCount);
        var include = GroupNameFromDraftIndex(draft.IncludeGroupFilterIndex, draft.GroupFilterOptions);
        var exclude = GroupNameFromDraftIndex(draft.ExcludeGroupFilterIndex, draft.GroupFilterOptions);
        var recentCount = NormalizeRecentDrawCount(draft.ExcludeRecentDrawCount);
        await _settings.UpdateAsync(settings =>
        {
            settings.DrawMode = mode;
            settings.DrawTargetCount = targetCount;
            settings.DrawIncludeGroupName = include;
            settings.DrawExcludeGroupName = exclude;
            settings.DrawExcludeRecentDrawCount = recentCount;
            settings.DrawOnlyNeverDrawn = draft.OnlyNeverDrawn;
            settings.DrawNoRepeatInRound = draft.NoRepeatInRound;
            settings.DrawSeatRegion = (SeatRegion)Math.Clamp(draft.SelectedSeatRegionIndex, 0, 4);
            settings.DrawSeatColumn = Math.Max(1, (int)Math.Round(draft.SeatColumnNumber));
            settings.DrawSeatRow = Math.Max(1, (int)Math.Round(draft.SeatRowNumber));
        });

        LoadRulesFromSettings();
        RuleDraft = null;
        await RefreshProbabilityPreviewAsync();
    }

    public void CancelRuleEdit() => RuleDraft = null;

    public async Task SavePresetAsync(string name)
    {
        var normalizedName = name.Trim();
        if (normalizedName.Length == 0)
        {
            throw new ArgumentException("预设名称不能为空。", nameof(name));
        }

        var settings = _settings.Current;
        var preset = new DrawPreset
        {
            Name = normalizedName,
            DrawMode = settings.DrawMode,
            TargetCount = settings.DrawTargetCount,
            IncludeGroupName = settings.DrawIncludeGroupName,
            ExcludeGroupName = settings.DrawExcludeGroupName,
            ExcludeRecentDrawCount = settings.DrawExcludeRecentDrawCount,
            OnlyNeverDrawn = settings.DrawOnlyNeverDrawn
        };
        var existing = _data.ActiveClassroom.DrawPresets.FindIndex(item => string.Equals(item.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) _data.ActiveClassroom.DrawPresets[existing] = preset;
        else _data.ActiveClassroom.DrawPresets.Add(preset);
        await _data.SaveAsync();
        RebuildPresets(normalizedName);
        StatusMessage = $"已保存预设“{normalizedName}”。";
    }

    public async Task ApplySelectedPresetAsync()
    {
        if (SelectedDrawPreset is null) return;
        var preset = SelectedDrawPreset;
        await _settings.UpdateAsync(settings =>
        {
            settings.DrawMode = preset.DrawMode;
            settings.DrawTargetCount = Math.Max(1, preset.TargetCount);
            settings.DrawIncludeGroupName = preset.IncludeGroupName;
            settings.DrawExcludeGroupName = preset.ExcludeGroupName;
            settings.DrawExcludeRecentDrawCount = Math.Max(0, preset.ExcludeRecentDrawCount);
            settings.DrawOnlyNeverDrawn = preset.OnlyNeverDrawn;
        });
        LoadRulesFromSettings();
        await RefreshProbabilityPreviewAsync();
        StatusMessage = $"已应用预设“{preset.Name}”。";
    }

    public async Task DeleteSelectedPresetAsync()
    {
        if (SelectedDrawPreset is null) return;
        var name = SelectedDrawPreset.Name;
        _data.ActiveClassroom.DrawPresets.RemoveAll(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        await _data.SaveAsync();
        RebuildPresets();
        StatusMessage = $"已删除预设“{name}”。";
    }

    internal void ScheduleRuleDraftRefresh(RuleDialogDraft draft)
    {
        if (ReferenceEquals(RuleDraft, draft))
        {
            _ = RefreshRuleDraftAsync(draft);
        }
    }

    private async Task CommitResultAsync(DrawResultItemViewModel item)
    {
        if (_currentDraw is null || !item.CanCommitResult)
        {
            return;
        }

        if (!TryStartActiveOperation(out var operationCompletion, out var operationCancellation))
        {
            StatusMessage = "已有抽取正在处理，请稍后再试。";
            return;
        }

        try
        {
            var prepared = _currentDraw;
            if (prepared is null)
            {
                return;
            }

            var isFinalConfirmation = Results
                .Where(row => !row.IsDiscarded)
                .All(row => ReferenceEquals(row, item) || row.IsCommitted);
            IReadOnlyList<Member> membersToCommit = isFinalConfirmation
                ? Results.Where(row => !row.IsDiscarded).Select(row => row.Member).ToList()
                : [item.Member];
            var outcome = await _execution.CommitAsync(
                prepared,
                membersToCommit,
                confirmSelection: true,
                notify: isFinalConfirmation,
                cancellationToken: operationCancellation.Token);
            operationCancellation.Token.ThrowIfCancellationRequested();
            item.MarkCommitted();
            StatusMessage = outcome.WasAutoReset
                ? $"{item.Name} 已确认，已自动重置权重。"
                : $"{item.Name} 已确认。";
            if (!outcome.ClassIslandSucceeded)
            {
                StatusMessage += " 通知发送失败。";
            }

            ReleaseDrawLeaseIfFullyCommitted();

            await RefreshProbabilityPreviewAsync(
                reloadWeights: false,
                cancellationToken: operationCancellation.Token);
        }
        catch
        {
            var prepared = _currentDraw;
            var committedBefore = Results
                .Where(row => row.IsCommitted && !row.IsDiscarded)
                .Select(row => row.Member)
                .ToList();
            try
            {
                if (prepared is not null)
                {
                    await _execution.RebuildCommittedStateAsync(prepared, committedBefore, CancellationToken.None);
                }
            }
            finally
            {
                Results.Clear();
                _currentDraw = null;
                ReleaseDrawLease();
                NotifyDrawStateChanged();
            }

            throw;
        }
        finally
        {
            FinishActiveOperation(operationCompletion, operationCancellation);
        }
    }

    private async Task DiscardResultAsync(DrawResultItemViewModel item)
    {
        if (_currentDraw is null || item.IsDiscarded)
        {
            return;
        }

        if (!TryStartActiveOperation(out var operationCompletion, out var operationCancellation))
        {
            StatusMessage = "已有抽取正在处理，请稍后再试。";
            return;
        }

        try
        {
            var prepared = _currentDraw;
            if (prepared is null)
            {
                return;
            }

            item.MarkDiscarded();
            Results.Remove(item);
            var committed = Results.Where(row => row.IsCommitted && !row.IsDiscarded).Select(row => row.Member).ToList();
            await _execution.RebuildCommittedStateAsync(prepared, committed, operationCancellation.Token);
            operationCancellation.Token.ThrowIfCancellationRequested();
            if (Results.Count == 0)
            {
                _currentDraw = null;
                ReleaseDrawLease();
            }
            else
            {
                ReleaseDrawLeaseIfFullyCommitted();
            }

            NotifyDrawStateChanged();
            StatusMessage = $"{item.Name} 已丢弃。";
            await RefreshProbabilityPreviewAsync(
                reloadWeights: false,
                cancellationToken: operationCancellation.Token);
        }
        catch
        {
            Results.Clear();
            _currentDraw = null;
            ReleaseDrawLease();
            NotifyDrawStateChanged();
            throw;
        }
        finally
        {
            FinishActiveOperation(operationCompletion, operationCancellation);
        }
    }

    /// <summary>课堂「换一个」：回滚该成员后重抽一人替换当前行。</summary>
    private async Task ReplaceResultAsync(DrawResultItemViewModel item)
    {
        if (_currentDraw is null || item.IsDiscarded)
        {
            return;
        }

        if (!TryStartActiveOperation(out var operationCompletion, out var operationCancellation))
        {
            StatusMessage = "已有抽取正在处理，请稍后再试。";
            return;
        }

        try
        {
            var prepared = _currentDraw;
            if (prepared is null)
            {
                return;
            }

            var outcome = await _execution.ReplaceAsync(
                prepared,
                item.Member,
                operationCancellation.Token);
            operationCancellation.Token.ThrowIfCancellationRequested();

            if (!outcome.Succeeded)
            {
                StatusMessage = outcome.Message ?? "没有可替换的候选人。";
                return;
            }

            // 用新成员替换当前行
            var index = Results.IndexOf(item);
            Results.Remove(item);
            if (outcome.Replacement is not null)
            {
                var row = new DrawResultItemViewModel(
                    outcome.Replacement,
                    new DrawnMemberWeight(
                        outcome.Replacement.Name,
                        outcome.Replacement.ExplicitWeight,
                        MemberRules.CalledWeight(outcome.Replacement.CalledCount)),
                    prepared.DrawMode,
                    isCommitted: true,
                    allowsManualCommit: false,
                    seatPosition: FindSeatPosition(outcome.Replacement),
                    CommitResultAsync,
                    DiscardResultAsync,
                    ReplaceResultAsync,
                    RateResultAsync);
                Results.Insert(Math.Max(0, index), row);
            }

            ReleaseDrawLeaseIfFullyCommitted();
            NotifyDrawStateChanged();
            StatusMessage = $"{item.Name} 已替换为 {outcome.Replacement?.Name ?? "新成员"}。";
            await RefreshProbabilityPreviewAsync(
                reloadWeights: false,
                cancellationToken: operationCancellation.Token);
        }
        catch
        {
            Results.Clear();
            _currentDraw = null;
            ReleaseDrawLease();
            NotifyDrawStateChanged();
            throw;
        }
        finally
        {
            FinishActiveOperation(operationCompletion, operationCancellation);
        }
    }

    /// <summary>
    /// 长期未点成员本次优先：把提醒名单中的成员权重临时乘以 3（不写存储）。
    /// 返回是否实际提升了权重，调用方负责在 finally 中恢复。
    /// </summary>
    private bool ApplyBoostedWeights()
    {
        if (_boostedNames.Count == 0)
        {
            return false;
        }

        var members = _data.ActiveClassroom.Members;
        foreach (var member in members)
        {
            if (!_boostedNames.Contains(member.Name.Trim()))
            {
                continue;
            }

            var original = member.ExplicitWeight;
            _boostedOriginalWeights[member.Name.Trim()] = original;
            member.ExplicitWeight = Math.Min(MemberRules.MaximumWeight, original * 3d);
        }

        return _boostedOriginalWeights.Count > 0;
    }

    /// <summary>恢复被临时提升的成员权重到原始值。</summary>
    private void RestoreBoostedWeights()
    {
        foreach (var member in _data.ActiveClassroom.Members)
        {
            if (_boostedOriginalWeights.TryGetValue(member.Name.Trim(), out var original))
            {
                member.ExplicitWeight = original;
            }
        }

        _boostedOriginalWeights.Clear();
    }

    /// <summary>
    /// 把本次提权成员的回滚快照改回真实权重。Prepare 在提权窗口内捕获的快照
    /// 若不修正，丢弃/重建时会把 3 倍临时权重持久化。
    /// </summary>
    private void CorrectBoostedSnapshots(PreparedDraw prepared)
    {
        foreach (var member in _data.ActiveClassroom.Members)
        {
            var normalizedName = member.Name.Trim();
            if (_boostedNames.Contains(normalizedName)
                && prepared.Snapshots.ContainsKey(normalizedName))
            {
                prepared.ReplaceSnapshot(normalizedName, DrawExecutionService.MemberSnapshot.Capture(member));
            }
        }
    }

    /// <summary>
    /// 刷新公平性提醒：排除本次会话已忽略的成员后，
    /// 重新计算长期未被点到的成员名单。
    /// </summary>
    internal void RefreshFairnessReminder()
    {
        var candidates = FairnessService.FindLongUncalled(
            _data.ActiveClassroom.Members,
            _data.ActiveClassroom.DrawHistory,
            _settings.Current.FairnessReminderDays);
        var reminder = candidates
            .Where(name => !_ignoredNames.Contains(name))
            .ToList();

        _boostedNames.Clear();
        foreach (var name in reminder)
        {
            _boostedNames.Add(name);
        }

        _longUncalledNames.Clear();
        _longUncalledNames.AddRange(reminder);
        OnPropertyChanged(nameof(LongUncalledNames));
        OnPropertyChanged(nameof(HasFairnessReminder));
        OnPropertyChanged(nameof(FairnessReminderText));
    }

    /// <summary>「本次优先」：点击后无需额外动作，提醒栏自动关闭，权重提升在下次抽取时生效。</summary>
    [RelayCommand]
    private void BoostForThisDraw()
    {
        DismissFairnessReminder();
        StatusMessage = "本次抽取将优先考虑长期未点到的成员。";
    }

    /// <summary>「忽略」：关闭提醒栏，且本次会话不再提醒这些成员。</summary>
    [RelayCommand]
    internal void DismissFairnessReminder()
    {
        foreach (var name in _longUncalledNames)
        {
            _ignoredNames.Add(name);
        }

        RefreshFairnessReminder();
    }

    /// <summary>
    /// 课堂表现评价：写入抽取记录；若设置开启评价影响权重，
    /// 同步调整成员权重并刷新概率预览。返回是否成功写入。
    /// </summary>
    private async Task<bool> RateResultAsync(DrawResultItemViewModel item, DrawRating rating)
    {
        if (_currentDraw is null)
        {
            return false;
        }

        var prepared = _currentDraw;
        prepared.HistoryEntry?.EnsureRecords();
        var recordExists = prepared.HistoryEntry?.Records.Any(record =>
            string.Equals(record.Name, item.Name, StringComparison.OrdinalIgnoreCase)) ?? false;
        if (!recordExists)
        {
            // 选择模式下未确认的成员还没有历史记录，无法评价
            return false;
        }

        var succeeded = await _execution.RateAsync(prepared, item.Name, rating);
        if (succeeded)
        {
            await RefreshProbabilityPreviewAsync(reloadWeights: false);
        }
        return succeeded;
    }

    [RelayCommand(CanExecute = nameof(CanDiscardCurrentDraw))]
    private async Task DiscardCurrentDrawAsync()
    {
        if (_currentDraw is null)
        {
            return;
        }

        if (!TryStartActiveOperation(out var operationCompletion, out var operationCancellation))
        {
            StatusMessage = "已有抽取正在处理，请稍后再试。";
            return;
        }

        try
        {
            var prepared = _currentDraw;
            if (prepared is null)
            {
                return;
            }

            await _execution.DiscardAsync(prepared, CancellationToken.None);
            foreach (var item in Results)
            {
                item.MarkDiscarded();
            }

            Results.Clear();
            _currentDraw = null;
            NotifyDrawStateChanged();
            StatusMessage = "本次抽取已丢弃。";
            await RefreshProbabilityPreviewAsync(
                reloadWeights: false,
                cancellationToken: operationCancellation.Token);
        }
        finally
        {
            ReleaseDrawLease();
            FinishActiveOperation(operationCompletion, operationCancellation);
        }
    }

    [RelayCommand(CanExecute = nameof(CanClearDrawFilters))]
    private void ClearDrawFilters()
    {
        IncludeGroupFilterIndex = 0;
        ExcludeGroupFilterIndex = 0;
        ExcludeRecentDrawCount = 0d;
        OnlyNeverDrawn = false;
        RefreshFilters();
    }

    private void RefreshFilters()
    {
        if (_synchronizingGroups)
        {
            return;
        }

        NotifyFilterStateChanged();
        _ = RefreshProbabilityPreviewAsync();
    }

    internal async Task RefreshProbabilityPreviewAsync(
        bool reloadWeights = true,
        CancellationToken cancellationToken = default)
    {
        var version = ++_previewRefreshVersion;
        if (reloadWeights)
        {
            await _data.ReloadImplicitWeightsAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var filtered = GetFilteredMembers(CurrentFilterOptions());
        var preview = _drawService.PreviewProbabilities(
            filtered,
            _data.ActiveClassroom.Groups,
            new DrawOptions(_settings.Current.DrawMode));
        if (version != _previewRefreshVersion)
        {
            return;
        }

        UpdateProbabilityPreview(ProbabilityPreview, preview);
        CandidateSummary = $"筛选候选 {filtered.Count} 人，可抽取 {preview.Count} 人";
        OnPropertyChanged(nameof(HasProbabilityPreview));
        OnPropertyChanged(nameof(ProbabilityPreviewVisibility));
        OnPropertyChanged(nameof(EmptyProbabilityPreviewVisibility));
        RefreshRoundProgress(filtered);
        RefreshRuleSummary();
    }

    /// <summary>刷新轮转进度。池已抽空时自动翻轮并提示。</summary>
    private void RefreshRoundProgress(IReadOnlyList<Member> filtered)
    {
        var round = _data.ActiveClassroom.Round;
        var enabled = _settings.Current.DrawNoRepeatInRound && round.DrawnNames.Count > 0;
        var baseFilters = CurrentFilterOptions();
        var progress = DrawFilterService.ComputeRoundProgress(
            _data.ActiveClassroom.Members,
            _data.ActiveClassroom.Groups,
            _data.ActiveClassroom.DrawHistory,
            baseFilters,
            round.DrawnNamesSet(),
            DrawFilterService.GetAbsentNames(_data.ActiveClassroom.Members));

        CurrentRoundNumber = round.RoundNumber;
        RoundDrawnCount = progress.DrawnCount;
        RoundDrawableCount = progress.DrawableCount;
        RoundModeEnabled = _settings.Current.DrawNoRepeatInRound;
        OnPropertyChanged(nameof(RoundProgressText));
        OnPropertyChanged(nameof(RoundProgressVisibility));
        OnPropertyChanged(nameof(RoundProgressValue));
        OnPropertyChanged(nameof(CanStartNextRound));
        StartNextRoundCommand.NotifyCanExecuteChanged();

        // 本轮已抽空：自动翻轮
        if (enabled && progress.NeedsNextRound && !_synchronizingGroups)
        {
            round.StartNextRound();
            _ = SaveRoundResetAsync();
            StatusMessage = $"第 {round.RoundNumber - 1} 轮已抽完，自动开始第 {round.RoundNumber} 轮。";
            CurrentRoundNumber = round.RoundNumber;
            RoundDrawnCount = 0;
            RoundDrawableCount = filtered.Count;
            OnPropertyChanged(nameof(RoundProgressText));
            OnPropertyChanged(nameof(RoundProgressValue));
        }
    }

    /// <summary>自动翻轮后立即落盘，避免退出应用时轮次状态回退。</summary>
    private async Task SaveRoundResetAsync()
    {
        try
        {
            await _data.SaveAsync();
        }
        catch
        {
            // 落盘失败不阻塞预览；下一次提交或手动翻轮会再次保存。
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartNextRound))]
    private async Task StartNextRoundAsync()
    {
        if (!await App.GetService<DialogService>().ConfirmAsync(
            "重开一轮",
            $"确定结束第 {CurrentRoundNumber} 轮并开始第 {CurrentRoundNumber + 1} 轮吗？已点过的成员将重新进入候选池。",
            "重开一轮"))
        {
            return;
        }

        _data.ActiveClassroom.Round.StartNextRound();
        await _data.SaveAsync();
        await RefreshProbabilityPreviewAsync(reloadWeights: false);
        StatusMessage = $"已开始第 {CurrentRoundNumber} 轮。";
    }

    private void ReleaseDrawLease()
    {
        _drawLease?.Dispose();
        _drawLease = null;
    }

    /// <summary>
    /// 全部结果已确认时释放抽取租约，避免选择模式确认完毕后下一次抽取被永久阻塞。
    /// 结果卡片保留（与自动提交模式一致），换一个/丢弃/评价仍可继续操作。
    /// </summary>
    private void ReleaseDrawLeaseIfFullyCommitted()
    {
        if (_drawLease is not null && Results.Count > 0 && Results.All(row => row.IsCommitted))
        {
            ReleaseDrawLease();
        }
    }

    private bool TryStartActiveOperation(
        out TaskCompletionSource completion,
        out CancellationTokenSource cancellation)
    {
        completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellation = new CancellationTokenSource();
        lock (_drawOperationGate)
        {
            if (_navigationRequested || _activeDrawTask is not null)
            {
                cancellation.Dispose();
                return false;
            }

            _activeDrawTask = completion.Task;
            _activeDrawCancellation = cancellation;
            return true;
        }
    }

    private void FinishActiveOperation(
        TaskCompletionSource completion,
        CancellationTokenSource cancellation,
        Exception? error = null)
    {
        lock (_drawOperationGate)
        {
            if (ReferenceEquals(_activeDrawTask, completion.Task))
            {
                _activeDrawTask = null;
                _activeDrawCancellation = null;
            }
        }

        cancellation.Dispose();
        if (error is null)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(error);
        }
    }

    private async Task RefreshRuleDraftAsync(RuleDialogDraft draft)
    {
        var version = ++_previewRefreshVersion;
        await _data.ReloadImplicitWeightsAsync();
        if (!ReferenceEquals(RuleDraft, draft))
        {
            return;
        }

        var mode = (DrawMode)Math.Clamp(draft.SelectedDrawModeIndex, 0, 3);
        var filtered = GetFilteredMembers(draft.FilterOptions());
        var preview = _drawService.PreviewProbabilities(filtered, _data.ActiveClassroom.Groups, new DrawOptions(mode));
        if (version != _previewRefreshVersion || !ReferenceEquals(RuleDraft, draft))
        {
            return;
        }

        UpdateProbabilityPreview(draft.ProbabilityPreview, preview);
        UpdateRuleSummary(
            draft.RuleSummaryRows,
            mode,
            NormalizeTargetCount(draft.TargetCount),
            filtered.Count,
            preview.Count,
            draft.FilterOptions());
    }

    private void RefreshRuleSummary()
    {
        var filtered = GetFilteredMembers(CurrentFilterOptions());
        UpdateRuleSummary(
            RuleSummaryRows,
            _settings.Current.DrawMode,
            NormalizeTargetCount(),
            filtered.Count,
            ProbabilityPreview.Count,
            CurrentFilterOptions());
    }

    private void UpdateRuleSummary(
        ObservableCollection<RuleSummaryRowViewModel> destination,
        DrawMode mode,
        int normalizedTargetCount,
        int filteredCount,
        int drawableCount,
        DrawFilterOptions filters)
    {
        destination.Clear();
        destination.Add(new RuleSummaryRowViewModel("当前模式", ModeText(mode), ModeDetail(mode)));
        destination.Add(new RuleSummaryRowViewModel("抽取人数", $"{normalizedTargetCount} 人", "每次按不放回方式抽取。"));
        destination.Add(new RuleSummaryRowViewModel("候选范围", $"筛选候选 {filteredCount} 人，可抽取 {drawableCount} 人", FilterSummary(filters)));
        destination.Add(new RuleSummaryRowViewModel("首轮概率", drawableCount > 0 ? "已生成" : "无可抽取候选", "多人抽取时，后续轮次会重新计算概率。"));
        destination.Add(new RuleSummaryRowViewModel("计数规则", CountRuleText(mode), CountRuleDetail(mode)));
        destination.Add(new RuleSummaryRowViewModel("小组加成", GroupBoostSummary(), "抽中启用联动的小组成员后，同组剩余成员会在后续轮次获得倍率。"));
    }

    private static void UpdateProbabilityPreview(
        ObservableCollection<DrawProbabilityPreviewRowViewModel> destination,
        IReadOnlyList<DrawProbabilityPreview> preview)
    {
        destination.Clear();
        foreach (var item in preview)
        {
            destination.Add(new DrawProbabilityPreviewRowViewModel(item));
        }
    }

    private IReadOnlyList<Member> GetFilteredMembers(DrawFilterOptions filters) =>
        DrawFilterService.Apply(
            _data.ActiveClassroom.Members,
            _data.ActiveClassroom.Groups,
            _data.ActiveClassroom.DrawHistory,
            filters,
            _data.ActiveClassroom.SeatMap);

    private DrawFilterOptions CurrentFilterOptions()
    {
        var settings = _settings.Current;
        var seatFilter = settings.DrawSeatRegion == SeatRegion.All
            ? null
            : new SeatFilterOptions(
                settings.DrawSeatRegion,
                ColumnIndex: settings.DrawSeatRegion == SeatRegion.SpecificColumn ? Math.Max(0, settings.DrawSeatColumn - 1) : -1,
                RowIndex: settings.DrawSeatRegion == SeatRegion.SpecificRow ? Math.Max(0, settings.DrawSeatRow - 1) : -1);
        return new DrawFilterOptions(
            GroupNameFromIndex(IncludeGroupFilterIndex),
            GroupNameFromIndex(ExcludeGroupFilterIndex),
            NormalizeRecentDrawCount(),
            OnlyNeverDrawn,
            SeatFilter: seatFilter);
    }

    private int NormalizeTargetCount() => NormalizeTargetCount(TargetCount);

    private static int NormalizeTargetCount(double value) =>
        double.IsFinite(value)
            ? Math.Clamp((int)Math.Round(value), 1, int.MaxValue)
            : 1;

    private int NormalizeRecentDrawCount() => NormalizeRecentDrawCount(ExcludeRecentDrawCount);

    private static int NormalizeRecentDrawCount(double value) =>
        double.IsFinite(value)
            ? Math.Clamp((int)Math.Round(value), 0, 10_000)
            : 0;

    private int NormalizeGroupIndex(int value) =>
        value > 0 && value < GroupFilterOptions.Count ? value : 0;

    private string? GroupNameFromIndex(int index) =>
        index > 0 && index < GroupFilterOptions.Count ? GroupFilterOptions[index] : null;

    private string FilterSummary(DrawFilterOptions filters)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(filters.IncludeGroupName)) parts.Add($"只抽小组：{filters.IncludeGroupName}");
        if (!string.IsNullOrWhiteSpace(filters.ExcludeGroupName)) parts.Add($"排除小组：{filters.ExcludeGroupName}");
        if (filters.ExcludeRecentDrawCount > 0) parts.Add($"排除最近 {filters.ExcludeRecentDrawCount} 次记录");
        if (filters.OnlyNeverDrawn) parts.Add("只抽从未出现在历史中的成员");
        return parts.Count == 0 ? "未启用筛选。" : string.Join("；", parts);
    }

    private string GroupBoostSummary()
    {
        var enabled = _data.ActiveClassroom.Groups.Count(group => group.IsDrawBoostEnabled && group.Members.Count > 0);
        return enabled == 0 ? "未配置启用联动的小组" : $"已启用 {enabled} 个小组";
    }

    private void RebuildGroupFilterOptions()
    {
        var include = GroupNameFromIndex(IncludeGroupFilterIndex);
        var exclude = GroupNameFromIndex(ExcludeGroupFilterIndex);
        _synchronizingGroups = true;
        GroupFilterOptions.Clear();
        GroupFilterOptions.Add("全部小组");
        foreach (var name in _data.ActiveClassroom.Groups
            .Select(group => group.GroupName.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            GroupFilterOptions.Add(name);
        }

        IncludeGroupFilterIndex = IndexOfGroup(include);
        ExcludeGroupFilterIndex = IndexOfGroup(exclude);
        RuleDraft?.UpdateGroupOptions(GroupFilterOptions);
        _synchronizingGroups = false;
        NotifyFilterStateChanged();
    }

    private void RebuildPresets(string? selectedName = null)
    {
        selectedName ??= SelectedDrawPreset?.Name;
        DrawPresets.Clear();
        foreach (var preset in _data.ActiveClassroom.DrawPresets
            .Where(preset => !string.IsNullOrWhiteSpace(preset.Name))
            .OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase))
        {
            DrawPresets.Add(preset.Clone());
        }
        SelectedDrawPreset = DrawPresets.FirstOrDefault(preset =>
            string.Equals(preset.Name, selectedName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task HandleGroupsChangedAsync()
    {
        var groupNames = _data.ActiveClassroom.Groups
            .Select(group => group.GroupName.Trim())
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settings = _settings.Current;
        var include = string.IsNullOrWhiteSpace(settings.DrawIncludeGroupName)
            || groupNames.Contains(settings.DrawIncludeGroupName.Trim())
            ? settings.DrawIncludeGroupName
            : null;
        var exclude = string.IsNullOrWhiteSpace(settings.DrawExcludeGroupName)
            || groupNames.Contains(settings.DrawExcludeGroupName.Trim())
            ? settings.DrawExcludeGroupName
            : null;
        if (!string.Equals(include, settings.DrawIncludeGroupName, StringComparison.Ordinal)
            || !string.Equals(exclude, settings.DrawExcludeGroupName, StringComparison.Ordinal))
        {
            await _settings.UpdateAsync(current =>
            {
                current.DrawIncludeGroupName = include;
                current.DrawExcludeGroupName = exclude;
            });
        }

        RebuildGroupFilterOptions();
        await RefreshProbabilityPreviewAsync();
    }

    private void LoadRulesFromSettings()
    {
        var settings = _settings.Current;
        _synchronizingGroups = true;
        SelectedDrawModeIndex = (int)settings.DrawMode;
        TargetCount = NormalizeTargetCount(settings.DrawTargetCount);
        IncludeGroupFilterIndex = IndexOfGroup(settings.DrawIncludeGroupName);
        ExcludeGroupFilterIndex = IndexOfGroup(settings.DrawExcludeGroupName);
        ExcludeRecentDrawCount = NormalizeRecentDrawCount(settings.DrawExcludeRecentDrawCount);
        OnlyNeverDrawn = settings.DrawOnlyNeverDrawn;
        _synchronizingGroups = false;
        NotifyFilterStateChanged();
    }

    private int IndexOfGroup(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? 0
            : Math.Max(0, GroupFilterOptions.IndexOf(name));

    private static string? GroupNameFromDraftIndex(int index, IList<string> options) =>
        index > 0 && index < options.Count ? options[index] : null;

    private void NotifyDrawStateChanged()
    {
        OnPropertyChanged(nameof(CanDiscardCurrentDraw));
        OnPropertyChanged(nameof(HasSelectedResults));
        DiscardCurrentDrawCommand.NotifyCanExecuteChanged();
    }

    private void NotifyFilterStateChanged()
    {
        OnPropertyChanged(nameof(CanClearDrawFilters));
        ClearDrawFiltersCommand.NotifyCanExecuteChanged();
    }

    private static IReadOnlyList<DrawnMemberWeight> CreateWeightRows(IEnumerable<Member> members) =>
        members.Select(member => new DrawnMemberWeight(
            member.Name,
            member.ExplicitWeight,
            MemberRules.CalledWeight(member.CalledCount))).ToList();

    private static string CreateCompletionMessage(DrawResult result, DrawCommitOutcome outcome)
    {
        var message = result.Message ?? $"本次抽取完成，共 {result.SelectedMembers.Count} 人。";
        if (outcome.WasAutoReset) message += " 已自动重置权重。";
        if (!outcome.ClassIslandSucceeded) message += " 通知发送失败。";
        return message;
    }

    private static string ModeText(DrawMode mode) => mode switch
    {
        DrawMode.SmartRandom => "智能随机",
        DrawMode.FullRandom => "完全随机",
        DrawMode.Selection => "选择模式",
        _ => "加权模式"
    };

    private static string ModeDetail(DrawMode mode) => mode switch
    {
        DrawMode.SmartRandom => "按被点次数自动调整随机倾向。",
        DrawMode.FullRandom => "基础随机部分对每位候选人保持均等。",
        DrawMode.Selection => "生成候选后，点击姓名确认计入次数。",
        _ => "按成员权重与被点次数计算。"
    };

    private static string CountRuleText(DrawMode mode) => mode switch
    {
        DrawMode.FullRandom => "不计入被点次数",
        DrawMode.Selection => "确认后计入",
        _ => "抽取完成后自动计入"
    };

    private static string CountRuleDetail(DrawMode mode) => mode switch
    {
        DrawMode.FullRandom => "结果仍会写入抽取历史。",
        DrawMode.Selection => "未确认或丢弃的结果不会影响次数。",
        _ => "结果会写入抽取历史，并参与自动重置设置。"
    };

    /// <summary>刷新今日考勤面板。请假成员不参与抽取。</summary>
    private void RefreshAttendance()
    {
        var classroom = _data.ActiveClassroom;
        AttendanceMembers.Clear();
        var today = DateOnly.FromDateTime(DateTime.Today);

        foreach (var member in classroom.Members
            .Where(member => member.IsActive && !string.IsNullOrWhiteSpace(member.Name))
            .OrderBy(member => member.Name, StringComparer.OrdinalIgnoreCase))
        {
            AttendanceMembers.Add(new AttendanceMemberViewModel(
                member,
                isAbsent: member.AbsentOn == today,
                onToggle: async (viewModel, isAbsent) =>
                {
                    member.AbsentOn = isAbsent ? today : null;
                    await _data.SaveAsync();
                    RefreshAttendance();
                    await RefreshProbabilityPreviewAsync(reloadWeights: false);
                }));
        }

        // 考勤面板只统计启用成员，直接从面板行计数，避免把停用成员的请假状态错算进请假总数
        AbsentCount = AttendanceMembers.Count(row => row.IsAbsent);
        PresentCount = AttendanceMembers.Count - AbsentCount;
        OnPropertyChanged(nameof(AttendanceSummary));
        OnPropertyChanged(nameof(HasAttendanceMembers));
        OnPropertyChanged(nameof(CanMarkAllPresent));
        MarkAllPresentCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanMarkAllPresent))]
    private async Task MarkAllPresentAsync()
    {
        foreach (var member in _data.ActiveClassroom.Members.Where(member => member.AbsentOn is not null))
        {
            member.AbsentOn = null;
        }

        await _data.SaveAsync();
        RefreshAttendance();
        await RefreshProbabilityPreviewAsync(reloadWeights: false);
        StatusMessage = "已标记全部成员到齐。";
    }

    public void InitializeAttendance() => RefreshAttendance();

    private string? FindSeatPosition(Member member)
    {
        var seatMap = _data.ActiveClassroom.SeatMap;
        if (seatMap is null)
        {
            return null;
        }

        var seat = SeatMapService.FindSeat(seatMap, member.Name);
        return seat is { } position
            ? $"{position.Column + 1}列{position.Row + 1}排"
            : null;
    }

    /// <summary>「查看所在座位」按钮可见性：班级已配置有效座位表（存在列）时可见。</summary>
    public Visibility HasSeatMapVisibility =>
        _data.ActiveClassroom.SeatMap is { Columns.Count: > 0 }
            ? Visibility.Visible
            : Visibility.Collapsed;
}

public sealed partial class AttendanceMemberViewModel : ObservableObject
{
    private readonly Action<AttendanceMemberViewModel, bool> _onToggle;
    private bool _initializing;

    public AttendanceMemberViewModel(Member member, bool isAbsent, Action<AttendanceMemberViewModel, bool> onToggle)
    {
        Member = member;
        _onToggle = onToggle;
        _initializing = true;
        IsAbsent = isAbsent;
        _initializing = false;
    }

    public Member Member { get; }
    public string Name => Member.Name;

    [ObservableProperty]
    private bool isAbsent;

    partial void OnIsAbsentChanged(bool value)
    {
        if (!_initializing)
        {
            _onToggle(this, value);
        }
    }
}

public partial class RuleDialogDraft : ObservableObject
{
    private readonly DrawPageViewModel _owner;
    private bool _initializing = true;

    public ObservableCollection<string> GroupFilterOptions { get; } = [];
    public ObservableCollection<DrawProbabilityPreviewRowViewModel> ProbabilityPreview { get; } = [];
    public ObservableCollection<RuleSummaryRowViewModel> RuleSummaryRows { get; } = [];
    public ObservableCollection<string> SeatRegionOptions { get; } =
    [
        "全部座位",
        "指定列",
        "指定排",
        "靠门半区",
        "靠窗半区"
    ];

    [ObservableProperty] private int selectedDrawModeIndex;
    [ObservableProperty] private double targetCount;
    [ObservableProperty] private int includeGroupFilterIndex;
    [ObservableProperty] private int excludeGroupFilterIndex;
    [ObservableProperty] private double excludeRecentDrawCount;
    [ObservableProperty] private bool onlyNeverDrawn;
    [ObservableProperty] private bool noRepeatInRound;
    [ObservableProperty] private int selectedSeatRegionIndex;
    [ObservableProperty] private double seatColumnNumber = 1d;
    [ObservableProperty] private double seatRowNumber = 1d;

    public Visibility SeatColumnVisibility => SelectedSeatRegionIndex == (int)SeatRegion.SpecificColumn ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SeatRowVisibility => SelectedSeatRegionIndex == (int)SeatRegion.SpecificRow ? Visibility.Visible : Visibility.Collapsed;

    public bool HasProbabilityPreview => ProbabilityPreview.Count > 0;
    public Visibility ProbabilityPreviewVisibility => HasProbabilityPreview ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyProbabilityPreviewVisibility => HasProbabilityPreview ? Visibility.Collapsed : Visibility.Visible;

    internal RuleDialogDraft(
        DrawPageViewModel owner,
        int modeIndex,
        double count,
        int includeIndex,
        int excludeIndex,
        double recentCount,
        bool neverDrawn,
        bool noRepeatInRound,
        int seatRegionIndex,
        double seatColumnNumber,
        double seatRowNumber,
        IEnumerable<string> groupOptions)
    {
        _owner = owner;
        GroupFilterOptions = new ObservableCollection<string>(groupOptions);
        selectedDrawModeIndex = modeIndex;
        targetCount = count;
        includeGroupFilterIndex = includeIndex;
        excludeGroupFilterIndex = excludeIndex;
        excludeRecentDrawCount = recentCount;
        onlyNeverDrawn = neverDrawn;
        this.noRepeatInRound = noRepeatInRound;
        selectedSeatRegionIndex = seatRegionIndex;
        this.seatColumnNumber = seatColumnNumber;
        this.seatRowNumber = seatRowNumber;
        _initializing = false;
    }

    public DrawFilterOptions FilterOptions() => new(
        GroupNameFromIndex(IncludeGroupFilterIndex),
        GroupNameFromIndex(ExcludeGroupFilterIndex),
        NormalizeRecentCount(ExcludeRecentDrawCount),
        OnlyNeverDrawn,
        SeatFilter: BuildSeatFilter());

    private SeatFilterOptions? BuildSeatFilter()
    {
        var region = (SeatRegion)Math.Clamp(SelectedSeatRegionIndex, 0, 4);
        if (region == SeatRegion.All)
        {
            return null;
        }

        return new SeatFilterOptions(
            region,
            ColumnIndex: region == SeatRegion.SpecificColumn ? Math.Max(0, (int)Math.Round(SeatColumnNumber) - 1) : -1,
            RowIndex: region == SeatRegion.SpecificRow ? Math.Max(0, (int)Math.Round(SeatRowNumber) - 1) : -1);
    }

    internal void UpdateGroupOptions(IEnumerable<string> groupOptions)
    {
        var include = GroupNameFromIndex(IncludeGroupFilterIndex);
        var exclude = GroupNameFromIndex(ExcludeGroupFilterIndex);
        GroupFilterOptions.Clear();
        foreach (var item in groupOptions) GroupFilterOptions.Add(item);
        IncludeGroupFilterIndex = IndexOf(include);
        ExcludeGroupFilterIndex = IndexOf(exclude);
        RequestRefresh();
    }

    partial void OnSelectedDrawModeIndexChanged(int value) => RequestRefresh();
    partial void OnTargetCountChanged(double value) => RequestRefresh();
    partial void OnIncludeGroupFilterIndexChanged(int value) => RequestRefresh();
    partial void OnExcludeGroupFilterIndexChanged(int value) => RequestRefresh();
    partial void OnExcludeRecentDrawCountChanged(double value) => RequestRefresh();
    partial void OnOnlyNeverDrawnChanged(bool value) => RequestRefresh();
    partial void OnSelectedSeatRegionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SeatColumnVisibility));
        OnPropertyChanged(nameof(SeatRowVisibility));
        RequestRefresh();
    }
    partial void OnSeatColumnNumberChanged(double value) => RequestRefresh();
    partial void OnSeatRowNumberChanged(double value) => RequestRefresh();

    private void RequestRefresh()
    {
        if (!_initializing)
        {
            _owner.ScheduleRuleDraftRefresh(this);
        }
    }

    private string? GroupNameFromIndex(int index) =>
        index > 0 && index < GroupFilterOptions.Count ? GroupFilterOptions[index] : null;

    private int IndexOf(string? groupName) =>
        string.IsNullOrWhiteSpace(groupName) ? 0 : Math.Max(0, GroupFilterOptions.IndexOf(groupName));

    private static int NormalizeRecentCount(double value) =>
        double.IsFinite(value) ? Math.Clamp((int)Math.Round(value), 0, 10_000) : 0;
}

public sealed class DrawProbabilityPreviewRowViewModel
{
    public DrawProbabilityPreviewRowViewModel(DrawProbabilityPreview preview)
    {
        Name = preview.Name;
        ProbabilityText = $"{preview.Probability:P1}";
        FactorsText = $"权重 {preview.ExplicitWeight:G6} · 被点权重 {preview.CalledWeight:G4} · 已点 {preview.CalledCount} 次 · 小组倍率 {preview.GroupMultiplier:G4}";
    }

    public string Name { get; }
    public string ProbabilityText { get; }
    public string FactorsText { get; }
}

public sealed record RuleSummaryRowViewModel(string Label, string Value, string Detail);


