using System.Text.Json;
using PickWeight.Core.Models;
using PickWeight.Core.Services;

namespace PickWeight.Core.Tests;

public sealed class PersistenceTests
{
    private static string CreateTempDirectory() =>
        Path.Combine(Path.GetTempPath(), $"PickWeight-{Guid.NewGuid():N}");

    private static async Task<JsonStateStore> CreateV2StoreAsync(string directory, AppSettings? settings = null)
    {
        Directory.CreateDirectory(directory);
        var store = new JsonStateStore(Path.Combine(directory, "state.json"));
        var state = new AppState
        {
            SchemaVersion = AppState.CurrentSchemaVersion,
            Settings = settings ?? new AppSettings(),
            ActiveClassroomName = Classroom.DefaultName,
            ClassroomNames = [Classroom.DefaultName]
        };
        await store.SaveAsync(state);
        var classroom = new Classroom
        {
            Name = Classroom.DefaultName,
            Members =
            [
                new Member
                {
                    Name = "Alice",
                    Gender = MemberGender.Female,
                    ExplicitWeight = 88d,
                    ImplicitWeight = 2d,
                    CalledCount = 3,
                    IsActive = false
                }
            ],
            Groups = [new Group { GroupName = "Team", Members = ["Alice"], IsDrawBoostEnabled = false }],
            DrawHistory =
            [
                new DrawHistoryEntry
                {
                    DrawnAt = new DateTimeOffset(2026, 6, 14, 10, 30, 0, TimeSpan.FromHours(8)),
                    MemberNames = ["Alice", "Bob"]
                }
            ]
        };
        await store.CreateClassroomAsync(classroom);
        return store;
    }

    [Fact]
    public async Task StateStore_RoundTripsState_WithoutRuntimeWeight()
    {
        var directory = CreateTempDirectory();
        try
        {
            var settings = new AppSettings
            {
                IsFirstRunCompleted = true,
                Theme = AppTheme.Dark,
                DrawMode = DrawMode.SmartRandom,
                DrawTargetCount = 3,
                DrawIncludeGroupName = "Team",
                DrawExcludeRecentDrawCount = 2,
                DrawOnlyNeverDrawn = true,
                ShowQuickDrawButton = false,
                ShowDrawNotifications = false,
                ShowClassIslandNotifications = false,
                AutoResetWeights = true,
                AutoResetDrawInterval = 5,
                DrawsSinceLastAutoReset = 3,
                BackgroundEffect = AppBackgroundEffect.Mica,
                AcrylicBlurAmount = 0.55d,
                AnimationSpeed = 1.5d,
                AnimationMotionScale = 0.8d,
                AnimationBounceScale = 1.2d
            };
            var store = await CreateV2StoreAsync(directory, settings);

            var loaded = await store.LoadAsync();
            var classroom = await store.LoadClassroomAsync(loaded.ActiveClassroomName);

            Assert.Equal(88d, classroom.Members[0].ExplicitWeight);
            Assert.False(classroom.Members[0].IsActive);
            Assert.Equal(MemberGender.Female, classroom.Members[0].Gender);
            Assert.Equal(1d, classroom.Members[0].ImplicitWeight);
            Assert.Equal("Alice", classroom.Groups[0].Members[0]);
            Assert.False(classroom.Groups[0].IsDrawBoostEnabled);
            Assert.Equal(["Alice", "Bob"], classroom.DrawHistory[0].MemberNames);
            Assert.Equal(2026, classroom.DrawHistory[0].DrawnAt.Year);
            Assert.Equal(AppTheme.Dark, loaded.Settings.Theme);
            Assert.True(loaded.Settings.IsFirstRunCompleted);
            Assert.Equal(DrawMode.SmartRandom, loaded.Settings.DrawMode);
            Assert.Equal(3, loaded.Settings.DrawTargetCount);
            Assert.Equal("Team", loaded.Settings.DrawIncludeGroupName);
            Assert.Equal(2, loaded.Settings.DrawExcludeRecentDrawCount);
            Assert.True(loaded.Settings.DrawOnlyNeverDrawn);
            Assert.False(loaded.Settings.ShowQuickDrawButton);
            Assert.False(loaded.Settings.ShowDrawNotifications);
            Assert.False(loaded.Settings.ShowClassIslandNotifications);
            Assert.True(loaded.Settings.AutoResetWeights);
            Assert.Equal(5, loaded.Settings.AutoResetDrawInterval);
            Assert.Equal(3, loaded.Settings.DrawsSinceLastAutoReset);
            Assert.Equal(AppBackgroundEffect.Mica, loaded.Settings.BackgroundEffect);
            Assert.Equal(0.55d, loaded.Settings.AcrylicBlurAmount);
            Assert.Equal(1.5d, loaded.Settings.AnimationSpeed);
            Assert.Equal(0.8d, loaded.Settings.AnimationMotionScale);
            Assert.Equal(1.2d, loaded.Settings.AnimationBounceScale);
            Assert.DoesNotContain("ImplicitWeight", await File.ReadAllTextAsync(Path.Combine(directory, "state.json")));
            var classroomFile = await File.ReadAllTextAsync(Path.Combine(store.GetClassroomDirectory(Classroom.DefaultName), "classroom.json"));
            Assert.Contains("\"Gender\": \"Female\"", classroomFile);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    // ---------- v1 → v2 迁移 ----------

    [Fact]
    public async Task Migrate_V1State_MovesDataIntoDefaultClassroomAndBacksUp()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "state.json");
        try
        {
            Directory.CreateDirectory(directory);
            var v1Content = """
                {
                  "members": [
                    { "name": "Alice", "gender": "Female", "explicitWeight": 88, "calledCount": 3 },
                    { "name": "Bob", "explicitWeight": 100 }
                  ],
                  "groups": [ { "groupName": "Team", "members": ["Alice"], "isDrawBoostEnabled": false } ],
                  "drawHistory": [ { "drawnAt": "2026-06-14T10:30:00+08:00", "memberNames": ["Alice"] } ],
                  "drawPresets": [ { "name": "提问", "targetCount": 2 } ],
                  "settings": { "drawMode": "SmartRandom", "isFirstRunCompleted": true }
                }
                """;
            await File.WriteAllTextAsync(path, v1Content);

            var store = new JsonStateStore(path);
            var loaded = await store.LoadAsync();

            Assert.Equal(AppState.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(Classroom.DefaultName, loaded.ActiveClassroomName);
            Assert.Equal([Classroom.DefaultName], loaded.ClassroomNames);
            Assert.Equal(DrawMode.SmartRandom, loaded.Settings.DrawMode);
            Assert.True(loaded.Settings.IsFirstRunCompleted);
            Assert.True(File.Exists(path + ".v1.bak"), "迁移前必须备份原文件");
            Assert.True(Directory.Exists(store.GetClassroomDirectory(Classroom.DefaultName)));
            Assert.True(File.Exists(Path.Combine(store.GetClassroomDirectory(Classroom.DefaultName), "implicit_config.json")));

            var classroom = await store.LoadClassroomAsync(loaded.ActiveClassroomName);
            Assert.Equal(2, classroom.Members.Count);
            Assert.Equal("Alice", classroom.Members[0].Name);
            Assert.Equal(MemberGender.Female, classroom.Members[0].Gender);
            Assert.Equal(88d, classroom.Members[0].ExplicitWeight);
            Assert.Equal(3, classroom.Members[0].CalledCount);
            Assert.Equal("Bob", classroom.Members[1].Name);
            Assert.Equal("Team", classroom.Groups[0].GroupName);
            Assert.False(classroom.Groups[0].IsDrawBoostEnabled);
            Assert.Equal("Alice", classroom.DrawHistory[0].MemberNames[0]);
            Assert.Equal("提问", classroom.DrawPresets[0].Name);
            Assert.Equal(2, classroom.DrawPresets[0].TargetCount);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Migrate_SecondLoad_DoesNotRunTwice()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "state.json");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, """{"members":[{"name":"Alice"}],"settings":{}}""");

            var store = new JsonStateStore(path);
            var first = await store.LoadAsync();
            var second = await store.LoadAsync();

            // 第二次加载不应再次迁移（无备份覆盖、无重复文件夹）
            Assert.Equal(AppState.CurrentSchemaVersion, second.SchemaVersion);
            Assert.Single(await store.ListClassroomNamesAsync());
            Assert.Single(Directory.GetFiles(directory, "*.v1.bak"));
            Assert.Equal(first.ActiveClassroomName, second.ActiveClassroomName);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Migrate_InvalidJson_FallsBackToReadOnlyMode()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "state.json");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, "{ not valid json");

            var loaded = await new JsonStateStore(path).LoadAsync();

            Assert.True(loaded.IsReadOnly);
            Assert.False(string.IsNullOrWhiteSpace(loaded.StartupNotice));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Load_MissingActiveClassroom_FallsBackToFirstAvailable()
    {
        var directory = CreateTempDirectory();
        try
        {
            var store = await CreateV2StoreAsync(directory);
            // 手工删除默认班级文件夹，模拟用户误删
            Directory.Delete(store.GetClassroomDirectory(Classroom.DefaultName), true);

            var loaded = await store.LoadAsync();

            Assert.Equal(Classroom.DefaultName, loaded.ActiveClassroomName);
            Assert.Single(loaded.ClassroomNames);
            Assert.False(string.IsNullOrWhiteSpace(loaded.StartupNotice));
            // 回退后自动重建默认班级
            Assert.True(Directory.Exists(store.GetClassroomDirectory(Classroom.DefaultName)));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    // ---------- 班级文件夹 CRUD ----------

    [Fact]
    public async Task Classroom_CreateRenameDelete_Works()
    {
        var directory = CreateTempDirectory();
        try
        {
            var store = await CreateV2StoreAsync(directory);

            await store.CreateClassroomAsync(new Classroom { Name = "高一(3)班", Members = [new Member { Name = "张三" }] });
            var names = await store.ListClassroomNamesAsync();
            Assert.Equal(2, names.Count);
            Assert.Contains("高一(3)班", names);

            await store.RenameClassroomAsync("高一(3)班", "高一(4)班");
            var renamed = await store.LoadClassroomAsync("高一(4)班");
            Assert.Equal("高一(4)班", renamed.Name);
            Assert.Equal("张三", renamed.Members[0].Name);
            Assert.False(Directory.Exists(store.GetClassroomDirectory("高一(3)班")));
            Assert.True(Directory.Exists(store.GetClassroomDirectory("高一(4)班")));

            await store.DeleteClassroomAsync("高一(4)班");
            var afterDelete = await store.ListClassroomNamesAsync();
            Assert.DoesNotContain("高一(4)班", afterDelete);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Classroom_RenameToExistingName_ThrowsAndKeepsOriginal()
    {
        var directory = CreateTempDirectory();
        try
        {
            var store = await CreateV2StoreAsync(directory);
            await store.CreateClassroomAsync(new Classroom { Name = "一班" });
            await store.CreateClassroomAsync(new Classroom { Name = "二班" });

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.RenameClassroomAsync("一班", "二班"));

            Assert.True(Directory.Exists(store.GetClassroomDirectory("一班")));
            Assert.True(Directory.Exists(store.GetClassroomDirectory("二班")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Classroom_CreateInvalidName_Throws()
    {
        var directory = CreateTempDirectory();
        try
        {
            var store = await CreateV2StoreAsync(directory);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.CreateClassroomAsync(new Classroom { Name = "a/b" }));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.CreateClassroomAsync(new Classroom { Name = "CON" }));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.CreateClassroomAsync(new Classroom { Name = string.Empty }));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    // ---------- 旧文件兼容 ----------

    [Fact]
    public async Task StateStore_OldSettings_DefaultClassIslandNotificationsToEnabled()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "state.json");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, """{"settings":{"showDrawNotifications":false}}""");

            var loaded = await new JsonStateStore(path).LoadAsync();

            Assert.True(loaded.Settings.ShowClassIslandNotifications);
            Assert.False(loaded.Settings.ShowDrawNotifications);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Classroom_Load_AlignsSeatColumnCounts()
    {
        var directory = CreateTempDirectory();
        try
        {
            var store = await CreateV2StoreAsync(directory);
            var classroom = await store.LoadClassroomAsync(Classroom.DefaultName);
            classroom.SeatMap = new SeatMap
            {
                Columns =
                [
                    new SeatColumn { SeatCount = 3, MemberNames = ["Alice", null, null] },  // 少一个 -> 补 null
                    new SeatColumn { SeatCount = 2, MemberNames = ["Alice", "Bob", "Carol"] } // 多一个 -> 截断
                ]
            };
            await store.SaveClassroomAsync(classroom);

            var reloaded = await store.LoadClassroomAsync(Classroom.DefaultName);
            Assert.Equal(3, reloaded.SeatMap!.Columns[0].MemberNames.Count);
            Assert.Equal("Alice", reloaded.SeatMap.Columns[0].MemberNames[0]);
            Assert.Equal(2, reloaded.SeatMap.Columns[1].MemberNames.Count);
            Assert.Equal(["Alice", "Bob"], reloaded.SeatMap.Columns[1].MemberNames);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    // ---------- 权重提供器 ----------

    [Fact]
    public async Task WeightProvider_IgnoresInvalidEntries()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new Dictionary<string, double>
            {
                ["Alice"] = 1.25d,
                ["Zero"] = 0d,
                ["Negative"] = -1d
            }));

            var result = await new JsonImplicitWeightProvider(path).LoadAsync();
            var weights = result.Weights;

            Assert.Equal(1.25d, weights["alice"]);
            Assert.DoesNotContain("Zero", weights.Keys);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WeightProvider_CreatesMissingConfig()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "implicit_config.json");
        try
        {
            var result = await new JsonImplicitWeightProvider(path).LoadAsync();

            Assert.True(result.WasCreated);
            Assert.True(File.Exists(path));
            Assert.Empty(result.Weights);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task WeightProviderFactory_CreatesPerClassroomConfigs()
    {
        var directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            var factory = new JsonImplicitWeightProviderFactory();

            var providerA = factory.Create(Path.Combine(directory, "一班"));
            var providerB = factory.Create(Path.Combine(directory, "二班"));

            var resultA = await providerA.SynchronizeAsync([new Member { Name = "Alice" }]);
            Assert.Equal(1d, resultA.Weights["Alice"]);

            // 二班没有 Alice 的配置，重新同步后只含自己的成员
            var resultB = await providerB.SynchronizeAsync([new Member { Name = "Bob" }]);
            Assert.Single(resultB.Weights);
            Assert.Contains("Bob", resultB.Weights.Keys);

            // 一班配置不受影响
            var reloadA = await providerA.LoadAsync();
            Assert.Contains("Alice", reloadA.Weights.Keys);
            Assert.Equal(Path.Combine(directory, "一班", "implicit_config.json"), resultA.FilePath);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task WeightProvider_Synchronize_PreservesMatchingAndRenamedValues_AndRemovesOthers()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "implicit_config.json");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, """
                {
                  " Alice ": 2.5,
                  "alice": 9,
                  "Former Name": 4,
                  "Removed": 3,
                  "Invalid": 0
                }
                """);
            var provider = new JsonImplicitWeightProvider(path);

            var result = await provider.SynchronizeAsync(
                [
                    new Member { Name = "Alice" },
                    new Member { Name = "New Name" },
                    new Member { Name = "Brand New" }
                ],
                new Dictionary<string, string> { ["Former Name"] = "New Name" });

            Assert.Equal(2.5d, result.Weights["Alice"]);
            Assert.Equal(4d, result.Weights["New Name"]);
            Assert.Equal(1d, result.Weights["Brand New"]);
            Assert.Equal(2, result.InvalidEntryCount);
            Assert.Equal(3, result.Weights.Count);

            var saved = JsonSerializer.Deserialize<Dictionary<string, double>>(await File.ReadAllTextAsync(path))!;
            Assert.Equal(3, saved.Count);
            Assert.DoesNotContain("Former Name", saved.Keys);
            Assert.DoesNotContain("Removed", saved.Keys);
            Assert.Equal(4d, saved["New Name"]);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task WeightProvider_Synchronize_RecoversMalformedConfigAndCreatesDefaults()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "implicit_config.json");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, "{ this is not valid JSON");
            var provider = new JsonImplicitWeightProvider(path);

            var result = await provider.SynchronizeAsync([new Member { Name = "Alice" }]);

            Assert.Equal(1, result.InvalidEntryCount);
            Assert.Equal(1d, result.Weights["Alice"]);
            var saved = JsonSerializer.Deserialize<Dictionary<string, double>>(await File.ReadAllTextAsync(path));
            Assert.Equal(1d, saved!["Alice"]);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
