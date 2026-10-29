using StartupScanner.Core.Models;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Writing;

public class ScheduledTaskToggleTests
{
    private static StartupItem Item(bool enabled = true, string taskPath = @"\Vendor\Updater") => new()
    {
        Id = Identity.Compute("task-scheduler", null, @"\Vendor", "Updater"),
        ProviderId = "task-scheduler", Location = @"\Vendor", Name = "Updater",
        RawValue = "updater.exe", Category = StartupCategory.ScheduledTask,
        State = enabled ? EnableState.Enabled : EnableState.Disabled,
        Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["TaskPath"] = taskPath },
    };

    private static (ScheduledTaskToggle Toggle, FakeTaskScheduler Tasks, FakeJournal Journal) Sut()
    {
        var tasks = new FakeTaskScheduler();
        return (new ScheduledTaskToggle(tasks, new FakeClock()), tasks, new FakeJournal());
    }

    [Fact]
    public void ProviderIdAndCapability()
    {
        var (toggle, _, _) = Sut();
        Assert.Equal("task-scheduler", toggle.ProviderId);
        Assert.Equal(ToggleCapability.Native, toggle.GetCapability(Item()));
    }

    [Fact]
    public void DisableCallsSetEnabledFalseOnTheFullTaskPath()
    {
        var (toggle, tasks, journal) = Sut();

        var record = toggle.SetEnabled(Item(), enabled: false, journal);

        Assert.Equal((@"\Vendor\Updater", false), tasks.SetEnabledCalls.Single());
        Assert.Equal("Task.Enabled", record.Mechanism);
        Assert.Equal(@"\Vendor\Updater", record.Location);
        Assert.Equal("sz:True", record.Before);
        Assert.Equal("sz:False", record.After);
        Assert.False(record.Enabled);
    }

    [Fact]
    public void EnableCallsSetEnabledTrue()
    {
        var (toggle, tasks, journal) = Sut();

        toggle.SetEnabled(Item(enabled: false), enabled: true, journal);

        Assert.Equal((@"\Vendor\Updater", true), tasks.SetEnabledCalls.Single());
    }

    [Fact]
    public void RestoreReappliesThePriorEnabledFlag()
    {
        var (toggle, tasks, journal) = Sut();
        var record = toggle.SetEnabled(Item(), enabled: false, journal);

        toggle.Restore(record);

        Assert.Equal((@"\Vendor\Updater", true), tasks.SetEnabledCalls[^1]);
    }

    [Fact]
    public void TaskPathIsReconstructedWhenTheExtraIsMissing()
    {
        var (toggle, tasks, journal) = Sut();
        var item = Item();
        var stripped = item with { Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) };

        toggle.SetEnabled(stripped, enabled: false, journal);

        Assert.Equal(@"\Vendor\Updater", tasks.SetEnabledCalls.Single().Path);
    }

    [Fact]
    public void APermissionFailurePropagatesSoNothingIsJournalled()
    {
        var (toggle, tasks, journal) = Sut();
        tasks.FailOn(@"\Vendor\Updater");

        Assert.Throws<UnauthorizedAccessException>(() => toggle.SetEnabled(Item(), enabled: false, journal));
        Assert.Empty(journal.All);
    }
}
