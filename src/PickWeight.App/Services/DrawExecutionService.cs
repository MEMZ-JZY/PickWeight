using PickWeight.Core.Abstractions;
using PickWeight.Core.Models;
using PickWeight.Core.Services;

namespace PickWeight_App.Services;

/// <summary>
/// Coordinates a prepared draw with its observable side effects.  Keeping the
/// draw service pure lets the main page and the quick-draw window use the same
/// candidate, history, count, reset and notification rules.
/// </summary>
public interface IDrawExecutionService
{
    Task<PreparedDraw> PrepareAsync(DrawExecutionRequest request, CancellationToken cancellationToken = default);

    Task<DrawCommitOutcome> CommitAsync(
        PreparedDraw prepared,
        IEnumerable<Member>? members = null,
        bool confirmSelection = false,
        bool notify = true,
        bool allowOutsideSelection = false,
        CancellationToken cancellationToken = default);

    Task RebuildCommittedStateAsync(
        PreparedDraw prepared,
        IEnumerable<Member> committedMembers,
        CancellationToken cancellationToken = default);

    Task DiscardAsync(PreparedDraw prepared, CancellationToken cancellationToken = default);

    /// <summary>
    /// 把已确认的成员替换为重新抽取的一名新成员（课堂「换一个」）。
    /// 会回滚原成员的被点次数与本轮已抽标记，再按相同规则抽取新成员。
    /// </summary>
    Task<DrawReplaceOutcome> ReplaceAsync(
        PreparedDraw prepared,
        Member replacedMember,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 为某位成员的抽取记录写入课堂表现评价。若设置开启评价影响权重，
    /// 会同步调整该成员当前的显式权重并标记 <see cref="Member.IsWeightAdjustedByRating"/>。
    /// 返回是否成功写入（成员在抽取历史中没有记录时返回 false）。
    /// </summary>
    Task<bool> RateAsync(
        PreparedDraw prepared,
        string memberName,
        DrawRating rating,
        CancellationToken cancellationToken = default);
}

public sealed record DrawReplaceOutcome(
    bool Succeeded,
    Member? Replacement,
    IReadOnlyList<string> CandidateNames,
    string? Message);

public sealed record DrawExecutionRequest(
    int TargetCount,
    DrawFilterOptions Filters,
    DrawMode? Mode = null,
    bool AutoCommitSelection = false);

public sealed class PreparedDraw
{
    internal PreparedDraw(
        DrawMode drawMode,
        DrawFilterOptions filters,
        DrawResult result,
        IReadOnlyList<string> candidateNames,
        Dictionary<string, DrawExecutionService.MemberSnapshot> snapshots,
        int drawsSinceLastAutoReset,
        bool autoCommitSelection,
        int roundNumber,
        IReadOnlyList<string> roundDrawnNames)
    {
        DrawMode = drawMode;
        Filters = filters;
        Result = result;
        CandidateNames = candidateNames;
        _snapshots = snapshots;
        DrawsSinceLastAutoReset = drawsSinceLastAutoReset;
        AutoCommitSelection = autoCommitSelection;
        RoundNumber = roundNumber;
        RoundDrawnNames = roundDrawnNames;
    }

    public DrawMode DrawMode { get; }
    public DrawFilterOptions Filters { get; }
    public DrawResult Result { get; }
    public IReadOnlyList<Member> SelectedMembers => Result.SelectedMembers;
    public IReadOnlyList<string> CandidateNames { get; }
    public bool HasCandidates => CandidateNames.Count > 0;
    public int RoundNumber { get; }

    /// <summary>Prepare 时刻的本轮已抽名单快照，用于回滚。</summary>
    internal IReadOnlyList<string> RoundDrawnNames { get; }

    internal IReadOnlyDictionary<string, DrawExecutionService.MemberSnapshot> Snapshots => _snapshots;

    /// <summary>
    /// 修正某位成员的回滚快照。调用方在 Prepare 期间临时改动过共享成员对象时
    /// （如「本次优先」提权），必须把真实值写回，否则回滚会把临时值持久化。
    /// </summary>
    internal void ReplaceSnapshot(string normalizedName, DrawExecutionService.MemberSnapshot snapshot) =>
        _snapshots[normalizedName] = snapshot;

    private readonly Dictionary<string, DrawExecutionService.MemberSnapshot> _snapshots;
    internal int DrawsSinceLastAutoReset { get; }
    internal bool AutoCommitSelection { get; }
    internal DrawHistoryEntry? HistoryEntry { get; set; }
    internal bool AutoResetRecorded { get; set; }
    internal bool WasAutoReset { get; set; }
    internal HashSet<string> CommittedNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>「换一个」替换掉的成员姓名（Trim 后），重建历史时用于恢复 WasReplaced 标记。</summary>
    internal HashSet<string> ReplacedNames { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record DrawCommitOutcome(
    bool Succeeded,
    bool WasAutoReset,
    bool ClassIslandSucceeded,
    IReadOnlyList<string> CommittedNames);

public sealed class DrawExecutionService : IDrawExecutionService
{
    private readonly AppDataService _data;
    private readonly IDrawService _drawService;
    private readonly ISettingsService _settings;
    private readonly NotificationService _notifications;
    private readonly IClassIslandNotificationBridge _classIslandNotifications;
    private readonly ObsOverlayService _obsOverlay;
    private readonly AppLogService _log;

    public DrawExecutionService(
        AppDataService data,
        IDrawService drawService,
        ISettingsService settings,
        NotificationService notifications,
        IClassIslandNotificationBridge classIslandNotifications,
        ObsOverlayService obsOverlay,
        AppLogService log)
    {
        _data = data;
        _drawService = drawService;
        _settings = settings;
        _notifications = notifications;
        _classIslandNotifications = classIslandNotifications;
        _obsOverlay = obsOverlay;
        _log = log;
    }

    public async Task<PreparedDraw> PrepareAsync(
        DrawExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        await _data.ReloadImplicitWeightsAsync(cancellationToken);

        var mode = request.Mode ?? _settings.Current.DrawMode;
        var classroom = _data.ActiveClassroom;
        var filters = AugmentFilters(classroom, request.Filters, _settings.Current.DrawNoRepeatInRound);
        var filteredMembers = DrawFilterService.Apply(
            classroom.Members,
            classroom.Groups,
            classroom.DrawHistory,
            filters,
            classroom.SeatMap);
        var preview = _drawService.PreviewProbabilities(
            filteredMembers,
            classroom.Groups,
            new DrawOptions(mode));
        var result = _drawService.Draw(
            filteredMembers,
            classroom.Groups,
            Math.Max(1, request.TargetCount),
            new DrawOptions(mode));

        // 若本轮已无可抽候选但仍有成员可抽，自动翻轮
        if (_settings.Current.DrawNoRepeatInRound
            && result.SelectedMembers.Count == 0
            && classroom.Round.DrawnNames.Count > 0
            && DrawFilterService.Apply(
                classroom.Members,
                classroom.Groups,
                classroom.DrawHistory,
                filters with { ExcludeRoundDrawnNames = null },
                classroom.SeatMap).Count > 0)
        {
            classroom.Round.StartNextRound();
            filters = AugmentFilters(classroom, request.Filters, _settings.Current.DrawNoRepeatInRound);
            filteredMembers = DrawFilterService.Apply(
                classroom.Members,
                classroom.Groups,
                classroom.DrawHistory,
                filters,
                classroom.SeatMap);
            preview = _drawService.PreviewProbabilities(
                filteredMembers,
                classroom.Groups,
                new DrawOptions(mode));
            result = _drawService.Draw(
                filteredMembers,
                classroom.Groups,
                Math.Max(1, request.TargetCount),
                new DrawOptions(mode));
        }

        return new PreparedDraw(
            mode,
            filters,
            result,
            preview.Select(item => item.Name).ToList(),
            CaptureSnapshots(classroom.Members),
            _data.State.Settings.DrawsSinceLastAutoReset,
            request.AutoCommitSelection,
            classroom.Round.RoundNumber,
            classroom.Round.DrawnNames.ToList());
    }

    /// <summary>
    /// 根据设置给过滤条件补充轮转排除与今日请假排除。
    /// 轮转排除仅在「一轮不重复」开启时注入本轮已抽名单，关闭时注入空集（与 null 语义等价）；
    /// 无论请求方传了什么过滤条件，请假成员始终不参与抽取。
    /// </summary>
    private static DrawFilterOptions AugmentFilters(Classroom classroom, DrawFilterOptions request, bool noRepeatInRound)
    {
        var roundNames = noRepeatInRound
            ? classroom.Round.DrawnNamesSet()
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var absentNames = DrawFilterService.GetAbsentNames(classroom.Members);
        return request with
        {
            ExcludeRoundDrawnNames = roundNames,
            ExcludeAbsentNames = absentNames
        };
    }

    public async Task<DrawCommitOutcome> CommitAsync(
        PreparedDraw prepared,
        IEnumerable<Member>? members = null,
        bool confirmSelection = false,
        bool notify = true,
        bool allowOutsideSelection = false,
        CancellationToken cancellationToken = default)
    {
        var selected = (members ?? prepared.SelectedMembers)
            .Where(member => allowOutsideSelection || prepared.SelectedMembers.Contains(member))
            .DistinctBy(member => Normalize(member.Name), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selected.Count == 0)
        {
            return new DrawCommitOutcome(false, prepared.WasAutoReset, true, []);
        }

        var shouldIncrement = prepared.DrawMode switch
        {
            DrawMode.FullRandom => false,
            DrawMode.Selection => confirmSelection || prepared.AutoCommitSelection,
            _ => true
        };

        if (shouldIncrement)
        {
            foreach (var member in selected.Where(member => prepared.CommittedNames.Add(Normalize(member.Name))))
            {
                MemberRules.IncrementCalledCount(member, prepared.DrawMode);
            }
        }
        else
        {
            foreach (var member in selected)
            {
                prepared.CommittedNames.Add(Normalize(member.Name));
            }
        }

        UpdateHistory(prepared);
        if (!prepared.AutoResetRecorded && prepared.CommittedNames.Count > 0 && ShouldRecordAutoReset(prepared.DrawMode))
        {
            prepared.AutoResetRecorded = true;
            prepared.WasAutoReset = AutoResetService.RecordSuccessfulDraw(_data.State.Settings, _data.ActiveClassroom.Members);
        }

        // 已确认的成员计入本轮已抽名单（用于一轮不重复）
        RecordRoundNames(prepared, selected);

        await _data.SaveAsync(cancellationToken);

        var names = selected.Select(member => member.Name).ToList();
        var classIslandSucceeded = true;
        if (notify)
        {
            _notifications.ShowDrawResult(names);
            classIslandSucceeded = (await _classIslandNotifications.ShowDrawResultAsync(names, cancellationToken)).Succeeded;
        }

        if (names.Count > 0)
        {
            await _obsOverlay.WriteLatestDrawAsync(names, cancellationToken);
            await _log.InfoAsync($"Committed draw: {names.Count} member(s); ClassIsland={classIslandSucceeded}.", cancellationToken);
        }
        return new DrawCommitOutcome(true, prepared.WasAutoReset, classIslandSucceeded, names);
    }

    public async Task RebuildCommittedStateAsync(
        PreparedDraw prepared,
        IEnumerable<Member> committedMembers,
        CancellationToken cancellationToken = default)
    {
        var originalDrawnAt = prepared.HistoryEntry?.DrawnAt;
        RestoreSnapshots(prepared.Snapshots);
        _data.State.Settings.DrawsSinceLastAutoReset = prepared.DrawsSinceLastAutoReset;
        if (prepared.HistoryEntry is not null)
        {
            _data.ActiveClassroom.DrawHistory.Remove(prepared.HistoryEntry);
            prepared.HistoryEntry = null;
        }

        // 恢复 Prepare 时刻的轮次状态
        _data.ActiveClassroom.Round.RoundNumber = prepared.RoundNumber;
        _data.ActiveClassroom.Round.DrawnNames = prepared.RoundDrawnNames.ToList();

        prepared.CommittedNames.Clear();
        prepared.AutoResetRecorded = false;
        prepared.WasAutoReset = false;

        var remaining = committedMembers.ToList();
        if (remaining.Count > 0)
        {
            // 「换一个」替补成员不在 SelectedMembers 中，重建必须绕过归属校验，
            // 否则替补的被点次数与历史会在回滚时被静默丢弃。
            await CommitAsync(
                prepared,
                remaining,
                confirmSelection: prepared.DrawMode == DrawMode.Selection,
                notify: false,
                allowOutsideSelection: true,
                cancellationToken: cancellationToken);

            // 重建会以当前时刻新建历史条目；恢复原抽取时间并补回替换标记，
            // 避免部分丢弃/离开页面后历史时间漂移、留痕丢失。
            if (prepared.HistoryEntry is not null)
            {
                if (originalDrawnAt is not null && prepared.HistoryEntry.DrawnAt != originalDrawnAt.Value)
                {
                    prepared.HistoryEntry.DrawnAt = originalDrawnAt.Value;
                    await _data.SaveAsync(cancellationToken);
                }

                if (prepared.ReplacedNames.Count > 0)
                {
                    prepared.HistoryEntry.EnsureRecords();
                    var restored = false;
                    foreach (var record in prepared.HistoryEntry.Records)
                    {
                        if (!prepared.ReplacedNames.Contains(record.Name.Trim()))
                        {
                            continue;
                        }

                        record.WasReplaced = true;
                        restored = true;
                    }

                    if (restored)
                    {
                        await _data.SaveAsync(cancellationToken);
                    }
                }
            }

            return;
        }

        await _data.SaveAsync(cancellationToken);
    }

    public Task DiscardAsync(PreparedDraw prepared, CancellationToken cancellationToken = default) =>
        RebuildCommittedStateAsync(prepared, [], cancellationToken);

    /// <summary>
    /// 课堂「换一个」：回滚被替换成员的全部副作用（被点次数、历史、本轮已抽标记），
    /// 然后按相同规则重新抽一人并直接提交。
    /// </summary>
    public async Task<DrawReplaceOutcome> ReplaceAsync(
        PreparedDraw prepared,
        Member replacedMember,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(replacedMember);

        // 1. 快照当前状态，以便替换失败时回滚
        var classroom = _data.ActiveClassroom;
        var classroomSnapshot = classroom.Clone();

        // 2. 回滚被替换成员的副作用
        var remaining = prepared.SelectedMembers
            .Where(member => !string.Equals(Normalize(member.Name), Normalize(replacedMember.Name), StringComparison.OrdinalIgnoreCase))
            .ToList();
        await RebuildCommittedStateAsync(prepared, remaining, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // 3. 重新抽取一人（排除被替换者，避免抽回原人）
        var filterNames = remaining
            .Concat([replacedMember])
            .Select(member => Normalize(member.Name))
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var drawFilters = prepared.Filters;
        var replacement = DrawReplacementCandidate(
            classroom,
            prepared.DrawMode,
            drawFilters,
            filterNames,
            _settings.Current.DrawNoRepeatInRound);

        if (replacement is null)
        {
            // 无可用候选，恢复原状
            RestoreClassroomSnapshot(classroom, classroomSnapshot);
            await _data.SaveAsync(cancellationToken);
            return new DrawReplaceOutcome(false, null, [], "没有可替换的候选人。");
        }

        // 4. 提交新成员（计入次数与历史）。替换出的新成员不在原 SelectedMembers 中，
        //    需以 allowOutsideSelection 绕过提交时的成员归属校验。
        var outcome = await CommitAsync(prepared, [replacement], notify: true, allowOutsideSelection: true, cancellationToken: cancellationToken);
        if (!outcome.Succeeded)
        {
            RestoreClassroomSnapshot(classroom, classroomSnapshot);
            await _data.SaveAsync(cancellationToken);
            return new DrawReplaceOutcome(false, null, [], "替换失败，已恢复原状。");
        }

        // 5. 标记原成员为被替换（历史留痕）
        MarkReplacedInHistory(prepared, replacedMember.Name);

        return new DrawReplaceOutcome(
            true,
            replacement,
            classroom.Round.DrawnNamesSet().ToArray(),
            null);
    }

    private Member? DrawReplacementCandidate(
        Classroom classroom,
        DrawMode mode,
        DrawFilterOptions filters,
        IReadOnlySet<string> excludedNames,
        bool noRepeatInRound)
    {
        // 排除被替换者本人与请假成员；替换抽取仍需遵守一轮不重复（仅在开关开启时）
        var roundNames = noRepeatInRound
            ? classroom.Round.DrawnNamesSet()
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var absentNames = DrawFilterService.GetAbsentNames(classroom.Members);
        var drawFilters = filters with
        {
            ExcludeAbsentNames = absentNames,
            ExcludeRoundDrawnNames = roundNames
        };

        var candidates = DrawFilterService.Apply(
            classroom.Members,
            classroom.Groups,
            classroom.DrawHistory,
            drawFilters,
            classroom.SeatMap)
            .Where(member => !excludedNames.Contains(Normalize(member.Name)))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        var result = _drawService.Draw(candidates, classroom.Groups, 1, new DrawOptions(mode));
        return result.SelectedMembers.FirstOrDefault();
    }

    private void MarkReplacedInHistory(PreparedDraw prepared, string replacedName)
    {
        prepared.ReplacedNames.Add(replacedName.Trim());

        var entry = prepared.HistoryEntry;
        if (entry is null)
        {
            return;
        }

        var record = entry.Records.FirstOrDefault(record =>
            string.Equals(record.Name, replacedName, StringComparison.OrdinalIgnoreCase));
        if (record is not null)
        {
            record.WasReplaced = true;
        }
    }

    /// <summary>
    /// 为抽取记录写入课堂表现评价。评价只作用于已确认（已提交历史）的成员；
    /// 若设置开启评价影响权重，同步调整成员当前的显式权重并落盘。
    /// </summary>
    public async Task<bool> RateAsync(
        PreparedDraw prepared,
        string memberName,
        DrawRating rating,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (string.IsNullOrWhiteSpace(memberName))
        {
            throw new ArgumentException("成员姓名不能为空。", nameof(memberName));
        }

        prepared.HistoryEntry?.EnsureRecords();
        var record = prepared.HistoryEntry?.Records.FirstOrDefault(item =>
            string.Equals(item.Name, memberName, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            return false;
        }

        record.Rating = rating;

        var member = _data.ActiveClassroom.Members.FirstOrDefault(item =>
            string.Equals(item.Name, memberName, StringComparison.OrdinalIgnoreCase));
        if (member is not null)
        {
            var settings = _settings.Current;
            if (settings.RatingAffectsWeight && rating is DrawRating.Excellent or DrawRating.Fair)
            {
                var multiplier = rating == DrawRating.Excellent
                    ? settings.RatingWeightMultiplier
                    : settings.RatingFairMultiplier;
                MemberRules.ApplyRatingAdjustment(member, rating, multiplier);
            }
        }

        await _data.SaveAsync(cancellationToken);
        return true;
    }

    private static void RestoreClassroomSnapshot(Classroom classroom, Classroom snapshot)
    {
        classroom.Members = snapshot.Members;
        classroom.Groups = snapshot.Groups;
        classroom.DrawHistory = snapshot.DrawHistory;
        classroom.DrawPresets = snapshot.DrawPresets;
        classroom.Round = snapshot.Round;
        classroom.SeatMap = snapshot.SeatMap;
        classroom.Subject = snapshot.Subject;
    }

    /// <summary>
    /// 把已确认成员写入本轮已抽名单。丢弃/回滚时由 <see cref="RebuildCommittedStateAsync"/> 的
    /// 快照恢复逻辑一并撤销（见 <see cref="RoundSnapshot"/>）。
    /// </summary>
    private void RecordRoundNames(PreparedDraw prepared, IReadOnlyList<Member> selected)
    {
        var round = _data.ActiveClassroom.Round;
        foreach (var member in selected)
        {
            var normalized = Normalize(member.Name);
            if (round.DrawnNames.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            round.DrawnNames.Add(normalized);
        }
    }

    private void UpdateHistory(PreparedDraw prepared)
    {
        var names = prepared.SelectedMembers
            .Where(member => prepared.CommittedNames.Contains(Normalize(member.Name)))
            .Select(member => member.Name)
            .ToList();

        // 「换一个」替换出的新成员不在 SelectedMembers 中，需并入历史（用班级名单还原显示名）
        if (names.Count < prepared.CommittedNames.Count)
        {
            var classroomByName = _data.ActiveClassroom.Members
                .GroupBy(member => Normalize(member.Name), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);
            names = [
                .. names,
                .. prepared.CommittedNames
                    .Where(name => !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    .Select(name => classroomByName.TryGetValue(name, out var display) ? display : name)
            ];
        }

        if (names.Count == 0)
        {
            return;
        }

        if (prepared.HistoryEntry is null)
        {
            prepared.HistoryEntry = new DrawHistoryEntry
            {
                DrawnAt = DateTimeOffset.Now,
                MemberNames = names,
                RoundNumber = prepared.RoundNumber
            };
            _data.ActiveClassroom.DrawHistory.Add(prepared.HistoryEntry);
            _ = StampClassContextAsync(prepared.HistoryEntry);
            return;
        }

        prepared.HistoryEntry.MemberNames = names;
    }

    /// <summary>
    /// 异步查询 ClassIsland 课时上下文并写入历史条目（科目、第几节）。
    /// 查询失败或未连接时静默跳过，不影响提交结果。
    /// </summary>
    private async Task StampClassContextAsync(DrawHistoryEntry entry)
    {
        try
        {
            var context = await _classIslandNotifications.QueryClassContextAsync(CancellationToken.None);
            if (context.Succeeded && context.Context is { } ctx)
            {
                entry.Subject = ctx.Subject;
                entry.PeriodIndex = ctx.PeriodIndex;
            }
        }
        catch
        {
            // 课时信息是增强字段，失败不阻塞
        }
    }

    private static bool ShouldRecordAutoReset(DrawMode mode) =>
        mode is DrawMode.Weighted or DrawMode.SmartRandom or DrawMode.Selection;

    private static Dictionary<string, MemberSnapshot> CaptureSnapshots(IEnumerable<Member> members) =>
        members
            .GroupBy(member => Normalize(member.Name), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0)
            .ToDictionary(
                group => group.Key,
                group => MemberSnapshot.Capture(group.First()),
                StringComparer.OrdinalIgnoreCase);

    private void RestoreSnapshots(IReadOnlyDictionary<string, MemberSnapshot> snapshots)
    {
        foreach (var member in _data.ActiveClassroom.Members)
        {
            if (snapshots.TryGetValue(Normalize(member.Name), out var snapshot))
            {
                snapshot.Restore(member);
            }
        }
    }

    private static string Normalize(string value) => value.Trim();

    internal sealed record MemberSnapshot(double ExplicitWeight, int CalledCount, bool IsWeightLinked)
    {
        public static MemberSnapshot Capture(Member member) =>
            new(member.ExplicitWeight, member.CalledCount, member.IsWeightLinked);

        public void Restore(Member member)
        {
            member.ExplicitWeight = ExplicitWeight;
            member.CalledCount = CalledCount;
            member.IsWeightLinked = IsWeightLinked;
        }
    }
}


