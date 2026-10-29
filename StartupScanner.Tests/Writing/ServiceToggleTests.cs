using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Writing;

public class ServiceToggleTests
{
    private const string Key = @"HKLM\SYSTEM\CurrentControlSet\Services\Spooler";

    private static StartupItem Item(int start, string name = "Spooler",
                                    StartupCategory category = StartupCategory.Service) => new()
    {
        Id = Identity.Compute("service", null, $@"HKLM\SYSTEM\CurrentControlSet\Services\{name}", name),
        ProviderId = "service",
        Location = $@"HKLM\SYSTEM\CurrentControlSet\Services\{name}",
        Name = name,
        RawValue = "svchost.exe",
        Category = category,
        State = start == 4 ? EnableState.Disabled : EnableState.Enabled,
        Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Start"] = start.ToString(), ["Type"] = "32",
        },
    };

    private static (ServiceToggle Toggle, FakeRegistry Registry, FakeJournal Journal) Sut()
    {
        var registry = new FakeRegistry();
        return (new ServiceToggle(registry, new FakeClock()), registry, new FakeJournal());
    }

    [Fact]
    public void ProviderIdAndCapability()
    {
        var (toggle, _, _) = Sut();
        Assert.Equal("service", toggle.ProviderId);
        Assert.Equal(ToggleCapability.Native, toggle.GetCapability(Item(2)));
    }

    [Fact]
    public void GuardrailedServicesAreBlocked()
    {
        var (toggle, _, _) = Sut();
        Assert.Equal(ToggleCapability.Blocked, toggle.GetCapability(Item(2, "RpcSs")));
        Assert.Equal(ToggleCapability.Blocked, toggle.GetCapability(Item(1, "disk", StartupCategory.Driver)));
    }

    [Fact]
    public void DisableSetsStartToFourAndJournalsThePriorValue()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(Key, "Start", 2, RegistryValueType.Dword);

        var record = toggle.SetEnabled(Item(2), enabled: false, journal);

        Assert.Equal(4, registry.OpenKey(Key)!.GetValue("Start"));
        Assert.Equal("Service.Start", record.Mechanism);
        Assert.Equal("Start", record.Name);
        Assert.Equal(Key, record.Location);
        Assert.Equal("dword:2", record.Before);
        Assert.Equal("dword:4", record.After);
        Assert.False(record.Enabled);
    }

    [Fact]
    public void EnableRestoresAutomaticVersusManualExactly()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(Key, "Start", 3, RegistryValueType.Dword);
        journal.Record(toggle.SetEnabled(Item(3), enabled: false, journal));

        toggle.SetEnabled(Item(4), enabled: true, journal);

        Assert.Equal(3, registry.OpenKey(Key)!.GetValue("Start"));
    }

    [Fact]
    public void EnableWithNoJournalledPriorValueFallsBackToManual()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(Key, "Start", 4, RegistryValueType.Dword);

        var record = toggle.SetEnabled(Item(4), enabled: true, journal);

        Assert.Equal(ServiceToggle.FallbackEnabledStart, registry.OpenKey(Key)!.GetValue("Start"));
        Assert.Equal("dword:3", record.After);
        Assert.True(record.Enabled);
    }

    [Fact]
    public void RestorePutsTheOldStartValueBack()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(Key, "Start", 2, RegistryValueType.Dword);
        var record = toggle.SetEnabled(Item(2), enabled: false, journal);

        toggle.Restore(record);

        Assert.Equal(2, registry.OpenKey(Key)!.GetValue("Start"));
    }

    [Fact]
    public void MissingServiceKeyThrows()
    {
        var (toggle, _, journal) = Sut();
        Assert.Throws<InvalidOperationException>(() => toggle.SetEnabled(Item(2), enabled: false, journal));
    }
}
