using PickWeight.Core.Abstractions;
using PickWeight.Core.Models;
using PickWeight.Core.Services;
using PickWeight.ClassIsland.Protocol;
using PickWeight_App.Services;
using PickWeight_App.ViewModels;

namespace PickWeight.App.Tests;

[Collection("PickWeight.App serial")]
public sealed class DrawPageViewModelTests
{
    [Fact]
    public async Task AutoCommittedDraw_ReleasesLeaseAndAllowsContinuousDraws()
    {
        using var fixture = DrawPageFixture.Create(DrawMode.Weighted);

        await fixture.ViewModel.DrawCommand.ExecuteAsync(null);
        using (var lease = fixture.Coordinator.TryAcquire())
        {
            Assert.NotNull(lease);
        }

        await fixture.ViewModel.DrawCommand.ExecuteAsync(null);

        Assert.Equal(2, fixture.Execution.CommitCount);
        Assert.Equal(2, fixture.Data.ActiveClassroom.DrawHistory.Count);
        var calledCount = fixture.Data.ActiveClassroom.Members[0].CalledCount;

        await fixture.ViewModel.OnNavigatedFromAsync();

        Assert.Equal(2, fixture.Data.ActiveClassroom.DrawHistory.Count);
        Assert.Equal(calledCount, fixture.Data.ActiveClassroom.Members[0].CalledCount);
    }

    [Fact]
    public async Task NavigationCleanup_PreservesConfirmedSelectionAndReleasesLeaseForReturn()
    {
        using var fixture = DrawPageFixture.Create(DrawMode.Selection);

        await fixture.ViewModel.DrawCommand.ExecuteAsync(null);
        await fixture.ViewModel.Results[0].CommitCommand.ExecuteAsync(null);
        var calledCount = fixture.Data.ActiveClassroom.Members[0].CalledCount;

        await fixture.ViewModel.OnNavigatedFromAsync();

        using (var lease = fixture.Coordinator.TryAcquire())
        {
            Assert.NotNull(lease);
        }
        Assert.Empty(fixture.ViewModel.Results);
        Assert.Equal(calledCount, fixture.Data.ActiveClassroom.Members[0].CalledCount);

        using var returned = DrawPageFixture.Create(
            DrawMode.Selection,
            fixture.Data,
            fixture.Settings,
            fixture.Coordinator);
        await returned.ViewModel.DrawCommand.ExecuteAsync(null);

        Assert.Single(returned.ViewModel.Results);
        await returned.ViewModel.OnNavigatedFromAsync();
    }

    [Fact]
    public async Task CommitFailure_ReleasesLease()
    {
        using var fixture = DrawPageFixture.Create(DrawMode.Weighted);
        fixture.Execution.ThrowOnCommit = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.ViewModel.DrawCommand.ExecuteAsync(null));

        using var lease = fixture.Coordinator.TryAcquire();
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task DiscardFailure_ReleasesLease()
    {
        using var fixture = DrawPageFixture.Create(DrawMode.Selection);
        await fixture.ViewModel.DrawCommand.ExecuteAsync(null);
        fixture.Execution.ThrowOnRebuild = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.ViewModel.DiscardCurrentDrawCommand.ExecuteAsync(null));

        using var lease = fixture.Coordinator.TryAcquire();
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task NavigationRebuildFailure_ReleasesLease()
    {
        using var fixture = DrawPageFixture.Create(DrawMode.Selection);
        await fixture.ViewModel.DrawCommand.ExecuteAsync(null);
        fixture.Execution.ThrowOnRebuild = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.ViewModel.OnNavigatedFromAsync());

        using var lease = fixture.Coordinator.TryAcquire();
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task NavigationDuringPrepare_CancelsActiveDrawAndReleasesLease()
    {
        using var fixture = DrawPageFixture.Create(DrawMode.Weighted);
        fixture.Execution.BlockPrepare = true;
        var drawTask = fixture.ViewModel.DrawCommand.ExecuteAsync(null);
        await fixture.Execution.PrepareStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await fixture.ViewModel.OnNavigatedFromAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drawTask);

        AssertLeaseAvailable(fixture.Coordinator);
        Assert.Empty(fixture.Data.ActiveClassroom.DrawHistory);
    }

    [Fact]
    public async Task NavigationDuringCommit_CancelsBeforeCommitAndReleasesLease()
    {
        using var fixture = DrawPageFixture.Create(DrawMode.Weighted);
        fixture.Execution.BlockCommit = true;
        var drawTask = fixture.ViewModel.DrawCommand.ExecuteAsync(null);
        await fixture.Execution.CommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await fixture.ViewModel.OnNavigatedFromAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drawTask);

        AssertLeaseAvailable(fixture.Coordinator);
        Assert.Empty(fixture.Data.ActiveClassroom.DrawHistory);
    }

    [Fact]
    public async Task NavigationDuringSelectionCommit_WaitsAndReleasesLease()
    {
        using var fixture = DrawPageFixture.Create(DrawMode.Selection);
        await fixture.ViewModel.DrawCommand.ExecuteAsync(null);
        fixture.Execution.BlockCommit = true;
        var commitTask = fixture.ViewModel.Results[0].CommitCommand.ExecuteAsync(null);
        await fixture.Execution.CommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await fixture.ViewModel.OnNavigatedFromAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commitTask);

        AssertLeaseAvailable(fixture.Coordinator);
        Assert.Empty(fixture.Data.ActiveClassroom.DrawHistory);
        Assert.Equal(0, fixture.Data.ActiveClassroom.Members[0].CalledCount);
    }

    [Fact]
    public async Task HasSelectedResults_ReflectsNonDiscardedResults()
    {
        using var fixture = DrawPageFixture.Create(DrawMode.Selection);
        Assert.False(fixture.ViewModel.HasSelectedResults);

        await fixture.ViewModel.DrawCommand.ExecuteAsync(null);
        Assert.True(fixture.ViewModel.HasSelectedResults);

        await fixture.ViewModel.Results[0].DiscardCommand.ExecuteAsync(null);
        Assert.False(fixture.ViewModel.HasSelectedResults);

        await fixture.ViewModel.OnNavigatedFromAsync();
    }

    [Fact]
    public async Task AutoCommittedDraw_ResultButtonsRemainUsable()
    {
        using var fixture = DrawPageFixture.Create(DrawMode.Weighted);
        fixture.Data.ActiveClassroom.Members.Add(new Member { Name = "李四" });
        await fixture.ViewModel.DrawCommand.ExecuteAsync(null);

        var item = fixture.ViewModel.Results[0];
        Assert.True(item.CanReplace);
        Assert.True(item.CanDiscardResult);
        var originalName = item.Name;

        // 非选择模式已自动提交的结果仍可「换一个」
        await item.ReplaceCommand.ExecuteAsync(null);

        Assert.Single(fixture.ViewModel.Results);
        Assert.NotEqual(originalName, fixture.ViewModel.Results[0].Name);
    }

    [Fact]
    public async Task RateSuccess_SetsRatingText()
    {
        using var fixture = DrawPageFixture.Create(DrawMode.Weighted);
        await fixture.ViewModel.DrawCommand.ExecuteAsync(null);

        var item = fixture.ViewModel.Results[0];
        Assert.False(item.HasRating);
        Assert.Equal(string.Empty, item.RatingText);

        await item.RateGoodCommand.ExecuteAsync(null);

        Assert.True(item.HasRating);
        Assert.Equal("已评价：良好", item.RatingText);
    }

    [Fact]
    public async Task RateFailure_DoesNotShowRatingText()
    {
        using var fixture = DrawPageFixture.Create(DrawMode.Selection);
        await fixture.ViewModel.DrawCommand.ExecuteAsync(null);

        var item = fixture.ViewModel.Results[0];
        // 选择模式下未确认成员没有历史记录，评价不成功，也不应假显示「已评价」
        await item.RateGoodCommand.ExecuteAsync(null);

        Assert.False(item.HasRating);
        Assert.Equal(string.Empty, item.RatingText);

        await fixture.ViewModel.OnNavigatedFromAsync();
    }

    [Fact]
    public async Task RateFair_AppliesSettingsFairMultiplier_WhenAffectsWeightEnabled()
    {
        using var fixture = DrawPageFixture.Create(DrawMode.Selection);
        fixture.Settings.Current.RatingAffectsWeight = true;
        fixture.Settings.Current.RatingFairMultiplier = 0.9d;
        await fixture.ViewModel.DrawCommand.ExecuteAsync(null);

        var item = fixture.ViewModel.Results[0];
        var originalWeight = item.Member.ExplicitWeight;
        await item.CommitCommand.ExecuteAsync(null);
        await item.RateFairCommand.ExecuteAsync(null);

        Assert.True(item.HasRating);
        Assert.Equal("已评价：一般", item.RatingText);
        Assert.True(item.Member.IsWeightAdjustedByRating);
        Assert.Equal(originalWeight * 0.9d, item.Member.ExplicitWeight, precision: 6);

        await fixture.ViewModel.OnNavigatedFromAsync();
    }

    [Fact]
    public async Task RateGood_AppliesSettingsWeightMultiplier_WhenAffectsWeightEnabled()
    {
        using var fixture = DrawPageFixture.Create(DrawMode.Selection);
        fixture.Settings.Current.RatingAffectsWeight = true;
        fixture.Settings.Current.RatingWeightMultiplier = 0.7d;
        await fixture.ViewModel.DrawCommand.ExecuteAsync(null);

        var item = fixture.ViewModel.Results[0];
        var originalWeight = item.Member.ExplicitWeight;
        await item.CommitCommand.ExecuteAsync(null);
        await item.RateGoodCommand.ExecuteAsync(null);

        Assert.True(item.HasRating);
        Assert.True(item.Member.IsWeightAdjustedByRating);
        Assert.Equal(originalWeight * 0.7d, item.Member.ExplicitWeight, precision: 6);

        await fixture.ViewModel.OnNavigatedFromAsync();
    }

    [Fact]
    public async Task SelectionDraw_AllConfirmed_ReleasesLeaseForNextDraw()
    {
        using var fixture = DrawPageFixture.Create(DrawMode.Selection);

        await fixture.ViewModel.DrawCommand.ExecuteAsync(null);
        await fixture.ViewModel.Results[0].CommitCommand.ExecuteAsync(null);

        // 全部确认后租约必须释放，否则此后所有抽取（含快捷窗口/热键）被永久阻塞
        using (var lease = fixture.Coordinator.TryAcquire())
        {
            Assert.NotNull(lease);
        }

        await fixture.ViewModel.DrawCommand.ExecuteAsync(null);

        // 新一次抽取产生未确认的新结果行，而不是停留在旧的已确认行上
        Assert.Single(fixture.ViewModel.Results);
        Assert.False(fixture.ViewModel.Results[0].IsCommitted);

        await fixture.ViewModel.OnNavigatedFromAsync();
    }

    [Fact]
    public async Task DiscardResult_WithBoostedWeights_RestoresOriginalWeight()
    {
        using var fixture = DrawPageFixture.Create(DrawMode.Weighted);
        var member = fixture.Data.ActiveClassroom.Members[0];
        var originalWeight = member.ExplicitWeight;

        // 张三从未被点过 → 公平性提醒生效 → 下次抽取前临时提权 3 倍
        fixture.ViewModel.RefreshFairnessReminder();
        Assert.True(fixture.ViewModel.HasFairnessReminder);

        await fixture.ViewModel.DrawCommand.ExecuteAsync(null);
        Assert.Equal(originalWeight, member.ExplicitWeight);

        // 丢弃必须回滚到真实快照，而不是把提权后的临时权重持久化
        await fixture.ViewModel.Results[0].DiscardCommand.ExecuteAsync(null);

        Assert.Equal(originalWeight, member.ExplicitWeight);
    }

    [Fact]
    public async Task DiscardOtherRow_AfterReplace_KeepsReplacementCommitted()
    {
        using var fixture = DrawPageFixture.Create(DrawMode.Weighted);
        fixture.Data.ActiveClassroom.Members.Add(new Member { Name = "李四" });
        fixture.Data.ActiveClassroom.Members.Add(new Member { Name = "王五" });
        fixture.ViewModel.TargetCount = 2d;

        await fixture.ViewModel.DrawCommand.ExecuteAsync(null);
        Assert.Equal(2, fixture.ViewModel.Results.Count);

        // 固定随机源：抽中张三和李四；「换一个」把张三替换为王五
        await fixture.ViewModel.Results[0].ReplaceCommand.ExecuteAsync(null);
        Assert.Equal("王五", fixture.ViewModel.Results[0].Name);
        Assert.Equal(1, fixture.Data.ActiveClassroom.Members.First(m => m.Name == "王五").CalledCount);

        // 丢弃另一行（李四）触发重建：替补王五的提交不得被归属校验静默回滚
        await fixture.ViewModel.Results[1].DiscardCommand.ExecuteAsync(null);

        Assert.Equal(1, fixture.Data.ActiveClassroom.Members.First(m => m.Name == "王五").CalledCount);
        var entry = Assert.Single(fixture.Data.ActiveClassroom.DrawHistory);
        Assert.Contains("王五", entry.MemberNames);

        await fixture.ViewModel.OnNavigatedFromAsync();
    }

    private static void AssertLeaseAvailable(DrawExecutionCoordinator coordinator)
    {
        using var lease = coordinator.TryAcquire();
        Assert.NotNull(lease);
    }

    private sealed class DrawPageFixture : IDisposable
    {
        private readonly string _overlayDirectory;

        private DrawPageFixture(
            AppDataService data,
            TestSettingsService settings,
            DrawExecutionCoordinator coordinator,
            RecordingExecutionService execution,
            DrawPageViewModel viewModel,
            string overlayDirectory)
        {
            Data = data;
            Settings = settings;
            Coordinator = coordinator;
            Execution = execution;
            ViewModel = viewModel;
            _overlayDirectory = overlayDirectory;
        }

        public AppDataService Data { get; }
        public TestSettingsService Settings { get; }
        public DrawExecutionCoordinator Coordinator { get; }
        public RecordingExecutionService Execution { get; }
        public DrawPageViewModel ViewModel { get; }

        public static DrawPageFixture Create(DrawMode mode)
        {
            var stateStore = new InMemoryStateStore();
            var data = new AppDataService(stateStore, new ConstantWeightProviderFactory());
            data.InitializeAsync().GetAwaiter().GetResult();
            data.ActiveClassroom.Members.Add(new Member { Name = "张三" });
            data.State.Settings.DrawMode = mode;
            data.State.Settings.DrawTargetCount = 1;
            var settings = new TestSettingsService(data);
            var coordinator = new DrawExecutionCoordinator();
            var overlayDirectory = Path.Combine(Path.GetTempPath(), "PickWeight.DrawPage.Tests", Guid.NewGuid().ToString("N"));
            var execution = CreateExecution(data, settings, overlayDirectory);
            var viewModel = new DrawPageViewModel(data, new DrawService(() => 0d), execution, settings, coordinator);
            return new DrawPageFixture(data, settings, coordinator, execution, viewModel, overlayDirectory);
        }

        public static DrawPageFixture Create(
            DrawMode mode,
            AppDataService data,
            TestSettingsService settings,
            DrawExecutionCoordinator coordinator)
        {
            data.State.Settings.DrawMode = mode;
            var overlayDirectory = Path.Combine(Path.GetTempPath(), "PickWeight.DrawPage.Tests", Guid.NewGuid().ToString("N"));
            var execution = CreateExecution(data, settings, overlayDirectory);
            var viewModel = new DrawPageViewModel(data, new DrawService(() => 0d), execution, settings, coordinator);
            return new DrawPageFixture(data, settings, coordinator, execution, viewModel, overlayDirectory);
        }

        private static RecordingExecutionService CreateExecution(
            AppDataService data,
            TestSettingsService settings,
            string overlayDirectory)
        {
            var outputPath = Path.Combine(overlayDirectory, "latest-draw.txt");
            var inner = new DrawExecutionService(
                data,
                new DrawService(() => 0d),
                settings,
                new NotificationService(settings),
                new TestClassIslandNotificationBridge(),
                new ObsOverlayService(outputPath),
                new AppLogService());
            return new RecordingExecutionService(inner);
        }

        public void Dispose()
        {
            if (Directory.Exists(_overlayDirectory))
            {
                Directory.Delete(_overlayDirectory, recursive: true);
            }
        }
    }

    private sealed class RecordingExecutionService : IDrawExecutionService
    {
        private readonly IDrawExecutionService _inner;

        public RecordingExecutionService(IDrawExecutionService inner) => _inner = inner;

        public int CommitCount { get; private set; }
        public bool ThrowOnCommit { get; set; }
        public bool ThrowOnRebuild { get; set; }
        public bool BlockPrepare { get; set; }
        public bool BlockCommit { get; set; }
        public TaskCompletionSource PrepareStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CommitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PreparedDraw> PrepareAsync(DrawExecutionRequest request, CancellationToken cancellationToken = default)
        {
            var prepared = await _inner.PrepareAsync(request, cancellationToken);
            PrepareStarted.TrySetResult();
            if (BlockPrepare)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return prepared;
        }

        public async Task<DrawCommitOutcome> CommitAsync(
            PreparedDraw prepared,
            IEnumerable<Member>? members = null,
            bool confirmSelection = false,
            bool notify = true,
            bool allowOutsideSelection = false,
            CancellationToken cancellationToken = default)
        {
            CommitCount++;
            CommitStarted.TrySetResult();
            if (ThrowOnCommit)
            {
                throw new InvalidOperationException("commit failed");
            }

            if (BlockCommit)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return await _inner.CommitAsync(prepared, members, confirmSelection, notify, allowOutsideSelection, cancellationToken);
        }

        public Task RebuildCommittedStateAsync(
            PreparedDraw prepared,
            IEnumerable<Member> committedMembers,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnRebuild)
            {
                throw new InvalidOperationException("rebuild failed");
            }

            return _inner.RebuildCommittedStateAsync(prepared, committedMembers, cancellationToken);
        }

        public Task<DrawReplaceOutcome> ReplaceAsync(
            PreparedDraw prepared,
            Member replacedMember,
            CancellationToken cancellationToken = default) =>
            _inner.ReplaceAsync(prepared, replacedMember, cancellationToken);
        public Task DiscardAsync(PreparedDraw prepared, CancellationToken cancellationToken = default)
        {
            if (ThrowOnRebuild)
            {
                throw new InvalidOperationException("rebuild failed");
            }

            return _inner.DiscardAsync(prepared, cancellationToken);
        }

        public Task<bool> RateAsync(
            PreparedDraw prepared,
            string memberName,
            DrawRating rating,
            CancellationToken cancellationToken = default) =>
            _inner.RateAsync(prepared, memberName, rating, cancellationToken);
    }

    private sealed class InMemoryStateStore : IStateStore
    {
        private AppState _state = new();

        public Task<AppState> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_state.Clone());

        public Task SaveAsync(AppState state, CancellationToken cancellationToken = default)
        {
            _state = state.Clone();
            return Task.CompletedTask;
        }

        public Task<Classroom> LoadClassroomAsync(string name, CancellationToken cancellationToken = default)
        {
            var classroom = _state.ClassroomNames.Contains(name)
                ? new Classroom { Name = name }
                : new Classroom();
            // 从内存班级字典读取
            if (_classrooms.TryGetValue(name, out var stored))
            {
                classroom = stored.Clone();
            }
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
            _state.ClassroomNames.Remove(name);
            return Task.CompletedTask;
        }

        public string GetClassroomDirectory(string name) => Path.Combine(Path.GetTempPath(), "in-memory", name);

    private readonly Dictionary<string, Classroom> _classrooms = new(StringComparer.OrdinalIgnoreCase);
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

    private sealed class ConstantWeightProviderFactory : IImplicitWeightProviderFactory
    {
        public IImplicitWeightProvider Create(string classroomDirectory) =>
            new ConstantWeightProvider();
    }

}
