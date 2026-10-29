using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class AppxStartupTaskProviderTests
{
    private const string SystemAppData =
        @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
    private const string Package = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0";

    private static (AppxStartupTaskProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new AppxStartupTaskProvider(registry, new FakeFileSystem()), registry);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU", UserSid = "S-1-5-21-9" };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("appx-startup", provider.Id);
        Assert.Equal(StartupCategory.AppxStartupTask, provider.Category);
    }

    [Fact]
    public void ReportsAnEnabledStartupTask()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{SystemAppData}\{Package}\SpotifyStartupTask", "State", 2, RegistryValueType.Dword);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal("SpotifyStartupTask", item.Name);
        Assert.Equal(Package, item.Extra["PackageFamilyName"]);
        Assert.Equal(EnableState.Enabled, item.State);
        Assert.Equal("Appx.State=2", item.StateSource);
        Assert.Equal("S-1-5-21-9", item.UserSid);
        Assert.False(item.PartiallyRead);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void StateValuesBelowTwoMeanDisabled(int state)
    {
        var (provider, registry) = Sut();
        registry.Set($@"{SystemAppData}\{Package}\Task", "State", state, RegistryValueType.Dword);

        Assert.Equal(EnableState.Disabled, Assert.Single(provider.Scan(Ctx())).State);
    }

    [Fact]
    public void SubkeysWithNoStateValueAreNotStartupTasks()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{SystemAppData}\{Package}\SomethingElse", "Other", 1, RegistryValueType.Dword);

        Assert.Empty(provider.Scan(Ctx()));
    }

    [Fact]
    public void AStateValueOfAnUnexpectedTypeDegradesToReadOnlyRatherThanGuessing()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{SystemAppData}\{Package}\Task", "State", "enabled", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal(EnableState.Unknown, item.State);
        Assert.True(item.PartiallyRead);
        Assert.Equal(ToggleCapability.Blocked,
            new AppxStartupTaskToggle(registry, new FakeClock()).GetCapability(item));
    }

    [Fact]
    public void AWellFormedTaskIsNativelyToggleable()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{SystemAppData}\{Package}\Task", "State", 2, RegistryValueType.Dword);
        var item = Assert.Single(provider.Scan(Ctx()));
        var toggle = new AppxStartupTaskToggle(registry, new FakeClock());

        Assert.Equal(ToggleCapability.Native, toggle.GetCapability(item));

        var record = toggle.SetEnabled(item, enabled: false, new FakeJournal());

        Assert.Equal(1, registry.OpenKey($@"{SystemAppData}\{Package}\Task")!.GetValue("State"));
        Assert.Equal("Appx.State", record.Mechanism);
        Assert.Equal("State", record.Name);
        Assert.Equal("dword:2", record.Before);
    }

    [Fact]
    public void EnablingWritesStateTwo()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{SystemAppData}\{Package}\Task", "State", 1, RegistryValueType.Dword);
        var item = Assert.Single(provider.Scan(Ctx()));

        new AppxStartupTaskToggle(registry, new FakeClock()).SetEnabled(item, enabled: true, new FakeJournal());

        Assert.Equal(2, registry.OpenKey($@"{SystemAppData}\{Package}\Task")!.GetValue("State"));
    }

    [Fact]
    public void MissingSystemAppDataKeyYieldsNothing()
    {
        var (provider, _) = Sut();
        Assert.Empty(provider.Scan(Ctx()));
    }
}
