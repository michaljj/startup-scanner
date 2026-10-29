using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class ScheduledTaskProviderTests
{
    private static ScheduledTaskInfo Task(string path, bool enabled = true) => new()
    {
        Path = path,
        Name = path[(path.LastIndexOf('\\') + 1)..],
        Folder = path[..path.LastIndexOf('\\')] is "" ? "\\" : path[..path.LastIndexOf('\\')],
        Enabled = enabled,
        Triggers = new[] { "AtLogon: any user", "Boot: delay 30s" },
        Actions = new[] { @"C:\Program Files\App\updater.exe /silent" },
        Principal = @"NT AUTHORITY\SYSTEM",
        RunLevel = "HighestAvailable",
        UserSid = null,
        LastRunTimeUtc = new DateTime(2026, 7, 29, 6, 0, 0, DateTimeKind.Utc),
        LastTaskResult = 0,
        NextRunTimeUtc = new DateTime(2026, 7, 31, 6, 0, 0, DateTimeKind.Utc),
        Xml = "<Task/>",
    };

    private static (ScheduledTaskProvider Provider, FakeTaskScheduler Tasks) Sut()
    {
        var tasks = new FakeTaskScheduler();
        // CommandLine.Split resolves an unquoted path containing spaces by probing the filesystem,
        // so the action's image has to exist for the split to land past "C:\Program".
        var fs = new FakeFileSystem();
        fs.AddFile(@"C:\Program Files\App\updater.exe", "MZ");
        return (new ScheduledTaskProvider(tasks, fs), tasks);
    }

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("task-scheduler", provider.Id);
        Assert.Equal(StartupCategory.ScheduledTask, provider.Category);
    }

    [Fact]
    public void ProjectsATaskIntoAnItemWithTypeSpecificFacts()
    {
        var (provider, tasks) = Sut();
        tasks.Add(Task(@"\Vendor\Updater"));

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }));

        Assert.Equal(@"\Vendor", item.Location);
        Assert.Equal("Updater", item.Name);
        Assert.Equal(@"C:\Program Files\App\updater.exe /silent", item.RawValue);
        Assert.Equal(@"C:\Program Files\App\updater.exe", item.ImagePath);
        Assert.Equal("/silent", item.Arguments);
        Assert.Equal(EnableState.Enabled, item.State);
        Assert.Equal("Task.Enabled", item.StateSource);
        Assert.Equal(@"\Vendor\Updater", item.Extra["TaskPath"]);
        Assert.Equal("AtLogon: any user; Boot: delay 30s", item.Extra["Triggers"]);
        Assert.Equal(@"C:\Program Files\App\updater.exe /silent", item.Extra["Actions"]);
        Assert.Equal(@"NT AUTHORITY\SYSTEM", item.Extra["Principal"]);
        Assert.Equal("HighestAvailable", item.Extra["RunLevel"]);
        Assert.Equal("0", item.Extra["LastTaskResult"]);
        Assert.Equal("2026-07-29T06:00:00Z", item.Extra["LastRunTimeUtc"]);
        Assert.Equal("2026-07-31T06:00:00Z", item.Extra["NextRunTimeUtc"]);
    }

    [Fact]
    public void DisabledTaskReadsAsDisabled()
    {
        var (provider, tasks) = Sut();
        tasks.Add(Task(@"\Vendor\Updater", enabled: false));

        Assert.Equal(EnableState.Disabled, Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" })).State);
    }

    [Fact]
    public void IdentityUsesTheFullTaskPathSoSameNamedTasksInDifferentFoldersDiffer()
    {
        var (provider, tasks) = Sut();
        tasks.Add(Task(@"\A\Updater"));
        tasks.Add(Task(@"\B\Updater"));

        var ids = provider.Scan(new ScanContext { UserRoot = "HKCU" }).Select(i => i.Id).Distinct().ToList();

        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public void PartiallyReadTaskIsFlagged()
    {
        var (provider, tasks) = Sut();
        tasks.Add(Task(@"\Protected\Thing") with { PartiallyRead = true, Actions = Array.Empty<string>() });

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }));

        Assert.True(item.PartiallyRead);
        Assert.Equal("", item.RawValue);
    }

    [Fact]
    public void MissingTimestampsRenderAsEmptyStringsRatherThanCrashing()
    {
        var (provider, tasks) = Sut();
        tasks.Add(Task(@"\A\B") with { LastRunTimeUtc = null, NextRunTimeUtc = null });

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }));

        Assert.Equal("", item.Extra["LastRunTimeUtc"]);
        Assert.Equal("", item.Extra["NextRunTimeUtc"]);
    }

    [Fact]
    public void EnumerationFailureBecomesAScanError()
    {
        var provider = new ScheduledTaskProvider(new ThrowingTaskScheduler(), new FakeFileSystem());
        var ctx = new ScanContext { UserRoot = "HKCU" };

        Assert.Empty(provider.Scan(ctx));
        Assert.Contains(ctx.Errors, e => e.ProviderId == "task-scheduler");
    }

    private sealed class ThrowingTaskScheduler : ITaskSchedulerService
    {
        public IReadOnlyList<ScheduledTaskInfo> EnumerateTasks() => throw new InvalidOperationException("COM failed");
        public void SetEnabled(string taskPath, bool enabled) => throw new InvalidOperationException();
    }
}
