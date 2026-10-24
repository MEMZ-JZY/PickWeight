using PickWeight.Core.Abstractions;
using PickWeight.Core.Models;
using PickWeight.Core.Services;
using PickWeight.ClassIsland.Protocol;
using PickWeight_App.Services;

namespace PickWeight.App.Tests;

/// <summary>
/// DrawExecutionService 事务路径（提交 / 回滚 / 重建 / 替换 / 自动重置 / 评价）的
/// 状态不变量测试。被点次数、历史记录、轮次名单与成员快照四者必须在每个
/// 入口之后保持一致；历史上最高严重度的缺陷全部集中在这些路径上。
/// </summary>
[Collection("PickWeight.App serial")]
public sealed class DrawExecutionTransactionTests
{
    [Fact]
    public async Task Commit_Weighted_IncrementsCountOnce_WritesHistoryAndRoundNames()
    {
        using var fixture = TransactionFixture.Create(members: ["张三"]);
        var member = fixture.Data.ActiveClassroom.Members[0];

        var prepared = await fixture.PrepareAsync();
        var outcome = await fixture.Execution.CommitAsync(prepared, prepared.SelectedMembers, confirmSelection: true);

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, member.CalledCount);
        var entry = Assert.Single(fixture.Data.ActiveClassroom.DrawHistory);
        Assert.Equal(["张三"], entry.MemberNames);
        Assert.Contains("张三", fixture.Data.ActiveClassroom.Round.DrawnNames);
    }

    [Fact]
    public async Task Commit_FullRandom_WritesHistoryWithoutCounting()
    {
        using var fixture = TransactionFixture.Create(members: ["张三"]);
        var member = fixture.Data.ActiveClassroom.Members[0];

        var prepared = await fixture.PrepareAsync(mode: DrawMode.FullRandom);
        var outcome = await fixture.Execution.CommitAsync(prepared, prepared.SelectedMembers, confirmSelection: true);

        Assert.True(outcome.Succeeded);
        Assert.Equal(0, member.CalledCount);
        var entry = Assert.Single(fixture.Data.ActiveClassroom.DrawHistory);
        Assert.Equal(["张三"], entry.MemberNames);
    }

    [Fact]
    public async Task Commit_Selection_Unconfirmed_WritesHistoryWithoutCounting()
    {
        using var fixture = TransactionFixture.Create(members: ["张三"]);
        var member = fixture.Data.ActiveClassroom.Members[0];

        var prepared = await fixture.PrepareAsync(mode: DrawMode.Selection);
        var outcome = await fixture.Execution.CommitAsync(prepared, prepared.SelectedMembers, confirmSelection: false);

        Assert.True(outcome.Succeeded);
        Assert.Equal(0, member.CalledCount);
        Assert.Single(fixture.Data.ActiveClassroom.DrawHistory);
    }

    [Fact]
    public async Task Commit_Selection_Confirmed_IncrementsCount()
    {
        using var fixture = TransactionFixture.Create(members: ["张三"]);
        var member = fixture.Data.ActiveClassroom.Members[0];

        var prepared = await fixture.PrepareAsync(mode: DrawMode.Selection);
        await fixture.Execution.CommitAsync(prepared, prepared.SelectedMembers, confirmSelection: true);

        Assert.Equal(1, member.CalledCount);
    }

    [Fact]
    public async Task Commit_DuplicateMembers_AreDeduplicated()
    {
        using var fixture = TransactionFixture.Create(members: ["张三"]);
        var member = fixture.Data.ActiveClassroom.Members[0];
        var prepared = await fixture.PrepareAsync();

        var duplicate = member.Clone();
        var outcome = await fixture.Execution.CommitAsync(
            prepared,
            [member, duplicate],
            confirmSelection: true);

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, member.CalledCount);
        var entry = Assert.Single(fixture.Data.ActiveClassroom.DrawHistory);
        Assert.Equal(["张三"], entry.MemberNames);
    }

    [Fact]
    public async Task DiscardAsync_RestoresCountsWeightsHistoryAndRound()
    {
        using var fixture = TransactionFixture.Create(members: ["张三"]);
        var member = fixture.Data.ActiveClassroom.Members[0];
        member.ExplicitWeight = 200d;
        member.IsWeightLinked = false;
        MemberRules.ChangeCalledCount(member, 1);
        fixture.Data.ActiveClassroom.DrawHistory.Add(new DrawHistoryEntry
        {
            DrawnAt = DateTimeOffset.Now.AddDays(-1),
            MemberNames = ["早前"]
        });
        fixture.Data.ActiveClassroom.Round.DrawnNames.Add("早前成员");

        var prepared = await fixture.PrepareAsync();
        await fixture.Execution.CommitAsync(prepared, prepared.SelectedMembers, confirmSelection: true);
        Assert.Equal(2, member.CalledCount);

        await fixture.Execution.DiscardAsync(prepared);

        Assert.Equal(1, member.CalledCount);
        Assert.Equal(200d, member.ExplicitWeight);
        Assert.False(member.IsWeightLinked);
        var remainingEntry = Assert.Single(fixture.Data.ActiveClassroom.DrawHistory);
        Assert.Equal(["早前"], remainingEntry.MemberNames);
        Assert.Equal(["早前成员"], fixture.Data.ActiveClassroom.Round.DrawnNames);
    }

    [Fact]
    public async Task Rebuild_PartialCommit_KeepsCommittedRestoresDiscarded()
    {
        using var fixture = TransactionFixture.Create(members: ["张三", "李四"]);
        fixture.Settings.Current.DrawTargetCount = 2;
        var 张三 = fixture.Data.ActiveClassroom.Members[0];
        var 李四 = fixture.Data.ActiveClassroom.Members[1];

        var prepared = await fixture.PrepareAsync(targetCount: 2);
        Assert.Equal(2, prepared.SelectedMembers.Count);
        await fixture.Execution.CommitAsync(prepared, prepared.SelectedMembers, confirmSelection: true);
        Assert.Equal(1, 张三.CalledCount);
        Assert.Equal(1, 李四.CalledCount);

        // 只保留张三的提交：李四的次数、历史与轮次标记必须完整回滚
        await fixture.Execution.RebuildCommittedStateAsync(prepared, [张三]);

        Assert.Equal(1, 张三.CalledCount);
        Assert.Equal(0, 李四.CalledCount);
        var entry = Assert.Single(fixture.Data.ActiveClassroom.DrawHistory);
        Assert.Equal(["张三"], entry.MemberNames);
        Assert.Equal(["张三"], fixture.Data.ActiveClassroom.Round.DrawnNames);
    }

    [Fact]
    public async Task Rebuild_PreservesOriginalDrawnAt()
    {
        using var fixture = TransactionFixture.Create(members: ["张三"]);
        var prepared = await fixture.PrepareAsync();
        await fixture.Execution.CommitAsync(prepared, prepared.SelectedMembers, confirmSelection: true);
        var original = Assert.Single(fixture.Data.ActiveClassroom.DrawHistory).DrawnAt;

        await fixture.Execution.RebuildCommittedStateAsync(prepared, prepared.SelectedMembers);

        var rebuilt = Assert.Single(fixture.Data.ActiveClassroom.DrawHistory);
        Assert.Equal(original, rebuilt.DrawnAt);
    }

    [Fact]
    public async Task Rebuild_KeepsReplacementCommitted_AfterReplace()
    {
        using var fixture = TransactionFixture.Create(members: ["张三", "李四", "王五"]);
        var 张三 = fixture.Data.ActiveClassroom.Members[0];

        var prepared = await fixture.PrepareAsync();
        await fixture.Execution.CommitAsync(prepared, prepared.SelectedMembers, confirmSelection: true);
        Assert.Equal(1, 张三.CalledCount);

        var outcome = await fixture.Execution.ReplaceAsync(prepared, 张三);
        Assert.True(outcome.Succeeded);
        var replacement = outcome.Replacement!;
        Assert.Equal(1, replacement.CalledCount);

        // 替补不在 prepared.SelectedMembers 中，重建必须绕过归属校验才能保留其提交
        await fixture.Execution.RebuildCommittedStateAsync(prepared, [replacement]);

        Assert.Equal(1, replacement.CalledCount);
        Assert.Equal(0, 张三.CalledCount);
        var entry = Assert.Single(fixture.Data.ActiveClassroom.DrawHistory);
        Assert.Equal([replacement.Name], entry.MemberNames);
    }

    [Fact]
    public async Task Replace_NoCandidates_RestoresOriginalState()
    {
        using var fixture = TransactionFixture.Create(members: ["张三"]);
        var member = fixture.Data.ActiveClassroom.Members[0];

        var prepared = await fixture.PrepareAsync();
        await fixture.Execution.CommitAsync(prepared, prepared.SelectedMembers, confirmSelection: true);

        var outcome = await fixture.Execution.ReplaceAsync(prepared, member);

        Assert.False(outcome.Succeeded);
        // 失败恢复必须原地还原成员，保持对象身份稳定，否则结果卡片持有的引用会失效
        Assert.Same(fixture.Data.ActiveClassroom.Members[0], member);
        Assert.Equal(1, member.CalledCount);
        var entry = Assert.Single(fixture.Data.ActiveClassroom.DrawHistory);
        Assert.Equal(["张三"], entry.MemberNames);
        Assert.Contains("张三", fixture.Data.ActiveClassroom.Round.DrawnNames);
    }

    [Fact]
    public async Task Commit_AutoReset_ResetsMembersAtInterval()
    {
        using var fixture = TransactionFixture.Create(
            settings => { settings.AutoResetWeights = true; settings.AutoResetDrawInterval = 1; },
            members: ["张三"]);
        fixture.Data.ActiveClassroom.Members[0].ExplicitWeight = 200d;
        var member = fixture.Data.ActiveClassroom.Members[0];

        var prepared = await fixture.PrepareAsync();
        var outcome = await fixture.Execution.CommitAsync(prepared, prepared.SelectedMembers, confirmSelection: true);

        Assert.True(outcome.WasAutoReset);
        Assert.Equal(MemberRules.DefaultExplicitWeight, member.ExplicitWeight);
        Assert.Equal(0, member.CalledCount);
        Assert.Equal(0, fixture.Data.State.Settings.DrawsSinceLastAutoReset);
        Assert.Single(fixture.Data.ActiveClassroom.DrawHistory);
    }

    [Fact]
    public async Task Rate_WritesRatingAndAdjustsWeight_WhenEnabled()
    {
        using var fixture = TransactionFixture.Create(
            settings => { settings.RatingAffectsWeight = true; settings.RatingFairMultiplier = 0.9d; },
            members: ["张三"]);
        var member = fixture.Data.ActiveClassroom.Members[0];

        var prepared = await fixture.PrepareAsync();
        await fixture.Execution.CommitAsync(prepared, prepared.SelectedMembers, confirmSelection: true);

        var succeeded = await fixture.Execution.RateAsync(prepared, "张三", DrawRating.Fair);

        Assert.True(succeeded);
        Assert.True(member.IsWeightAdjustedByRating);
        Assert.Equal(90d, member.ExplicitWeight, precision: 6);
        Assert.Equal(DrawRating.Fair, Assert.Single(Assert.Single(fixture.Data.ActiveClassroom.DrawHistory).Records).Rating);
    }

    [Fact]
    public async Task Rate_UnknownMember_ReturnsFalse()
    {
        using var fixture = TransactionFixture.Create(members: ["张三"]);
        var prepared = await fixture.PrepareAsync();
        await fixture.Execution.CommitAsync(prepared, prepared.SelectedMembers, confirmSelection: true);

        var succeeded = await fixture.Execution.RateAsync(prepared, "不存在", DrawRating.Fair);

        Assert.False(succeeded);
    }

    [Fact]
    public async Task SequentialCycles_CountsAccumulateWithFreshSnapshots()
    {
        using var fixture = TransactionFixture.Create(members: ["张三"]);
        var member = fixture.Data.ActiveClassroom.Members[0];

        for (var expected = 1; expected <= 2; expected++)
        {
            var prepared = await fixture.PrepareAsync();
            await fixture.Execution.CommitAsync(prepared, prepared.SelectedMembers, confirmSelection: true);
            Assert.Equal(expected, member.CalledCount);
        }

        Assert.Equal(2, fixture.Data.ActiveClassroom.DrawHistory.Count);
    }

    // ---------- 测试基建 ----------

    private sealed class TransactionFixture : IDisposable
    {
        private readonly string _logDirectory;

        private TransactionFixture(
            AppDataService data,
            TestSettingsService settings,
            DrawExecutionService execution,
            string logDirectory)
        {
            Data = data;
            Settings = settings;
            Execution = execution;
            _logDirectory = logDirectory;
        }

        public AppDataService Data { get; }
        public TestSettingsService Settings { get; }
        public DrawExecutionService Execution { get; }

        public Task<PreparedDraw> PrepareAsync(
            int targetCount = 1,
            DrawMode mode = DrawMode.Weighted) =>
            Execution.PrepareAsync(new DrawExecutionRequest(targetCount, new DrawFilterOptions(), mode));

        public static TransactionFixture Create(Action<AppSettings>? configure = null, params string[] members)
        {
            var stateStore = new InMemoryStateStore();
            var data = new AppDataService(stateStore, new ConstantWeightProviderFactory());
            data.InitializeAsync().GetAwaiter().GetResult();
            foreach (var name in members.Length == 0 ? ["张三"] : members)
            {
                data.ActiveClassroom.Members.Add(new Member { Name = name });
            }

            configure?.Invoke(data.State.Settings);
            var logDirectory = Path.Combine(
                Path.GetTempPath(),
                "PickWeight.Transaction.Tests",
                Guid.NewGuid().ToString("N"));
            var settings = new TestSettingsService(data);
            var execution = new DrawExecutionService(
                data,
                new DrawService(() => 0d),
                settings,
                new NotificationService(settings),
                new TestClassIslandNotificationBridge(),
                new ObsOverlayService(Path.Combine(logDirectory, "latest-draw.txt")),
                new AppLogService(logDirectory));
            return new TransactionFixture(data, settings, execution, logDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_logDirectory))
            {
                Directory.Delete(_logDirectory, recursive: true);
            }
        }
    }

    private sealed class InMemoryStateStore : IStateStore
    {
        private AppState _state = new();
        private readonly Dictionary<string, Classroom> _classrooms = new(StringComparer.OrdinalIgnoreCase);

        public Task<AppState> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_state.Clone());

        public Task SaveAsync(AppState state, CancellationToken cancellationToken = default)
        {
            _state = state.Clone();
            return Task.CompletedTask;
        }

        public Task<Classroom> LoadClassroomAsync(string name, CancellationToken cancellationToken = default)
        {
            var classroom = _classrooms.TryGetValue(name, out var stored)
                ? stored.Clone()
                : new Classroom { Name = name };
            return Task.FromResult(classroom);
        }

        public Task SaveClassroomAsync(Classroom classroom, CancellationToken cancellationToken = default)
        {
            _classrooms[classroom.Name] = classroom.Clone();
            if (!_state.ClassroomNames.Contains(classroom.Name))
            {
                _state.ClassroomNames.Add(classroom.Name);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListClassroomNamesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(_state.ClassroomNames.ToList());

        public Task CreateClassroomAsync(Classroom classroom, CancellationToken cancellationToken = default)
        {
            _classrooms[classroom.Name] = classroom.Clone();
            _state.ClassroomNames.Add(classroom.Name);
            return Task.CompletedTask;
        }

        public Task RenameClassroomAsync(string oldName, string newName, CancellationToken cancellationToken = default)
        {
            if (_classrooms.TryGetValue(oldName, out var classroom))
            {
                _classrooms.Remove(oldName);
                classroom.Name = newName;
                _classrooms[newName] = classroom;
            }

            _state.ClassroomNames.Remove(oldName);
            _state.ClassroomNames.Add(newName);
            return Task.CompletedTask;
        }

        public Task DeleteClassroomAsync(string name, CancellationToken cancellationToken = default)
        {
            _classrooms.Remove(name);
            return Task.CompletedTask;
        }

        public string GetClassroomDirectory(string name) => Path.Combine(Path.GetTempPath(), "in-memory", name);
    }

    private sealed class ConstantWeightProvider : IImplicitWeightProvider
    {
        public Task<ImplicitWeightLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImplicitWeightLoadResult(new Dictionary<string, double>(), false, 0, string.Empty));

        public Task<ImplicitWeightLoadResult> SynchronizeAsync(
            IReadOnlyCollection<Member> members,
            IReadOnlyDictionary<string, string>? renamedMembers = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImplicitWeightLoadResult(
                members.ToDictionary(member => member.Name, _ => 1d),
                false,
                0,
                string.Empty));
    }

    private sealed class ConstantWeightProviderFactory : IImplicitWeightProviderFactory
    {
        public IImplicitWeightProvider Create(string classroomDirectory) => new ConstantWeightProvider();
    }

    private sealed class TestClassIslandNotificationBridge : IClassIslandNotificationBridge
    {
        public Task<ClassIslandSendResult> ShowDrawResultAsync(
            IEnumerable<string> names,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClassIslandSendResult(true));

        public Task<ClassContextResult> QueryClassContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClassContextResult(false, null, "not connected"));
    }

    private sealed class TestSettingsService : ISettingsService
    {
        private readonly AppDataService _data;

        public TestSettingsService(AppDataService data) => _data = data;

        public AppSettings Current => _data.State.Settings;

        public event EventHandler<SettingsChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public Task UpdateAsync(Action<AppSettings> update)
        {
            update(Current);
            return Task.CompletedTask;
        }
    }
}
