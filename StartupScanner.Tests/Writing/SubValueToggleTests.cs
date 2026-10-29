using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Writing;

public class SubValueToggleTests
{
    private const string Key = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\sethc.exe";

    private static StartupItem Item() => new()
    {
        Id = Identity.Compute("ifeo", null, Key, "sethc.exe:Debugger"),
        ProviderId = "ifeo", Location = Key, Name = "sethc.exe:Debugger",
        RawValue = @"C:\Windows\System32\cmd.exe", Category = StartupCategory.Ifeo, State = EnableState.Enabled,
        Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TargetImage"] = "sethc.exe", ["ValueName"] = "Debugger",
        },
    };

    [Fact]
    public void DisableRemovesTheValueNamedByTheExtraNotTheCompositeItemName()
    {
        var registry = new FakeRegistry();
        registry.Set(Key, "Debugger", @"C:\Windows\System32\cmd.exe", RegistryValueType.String);
        var toggle = new SubValueToggle("ifeo", registry, new FakeClock());

        var record = toggle.SetEnabled(Item(), enabled: false, new FakeJournal());

        Assert.Null(registry.OpenKey(Key)!.GetValue("Debugger"));
        Assert.Equal("Debugger", record.Name);
        Assert.Equal(@"sz:C:\Windows\System32\cmd.exe", record.Before);
    }

    [Fact]
    public void EnableRestoresIntoTheSameSubValue()
    {
        var registry = new FakeRegistry();
        registry.Set(Key, "Debugger", @"C:\evil.exe", RegistryValueType.String);
        var toggle = new SubValueToggle("ifeo", registry, new FakeClock());
        var journal = new FakeJournal();
        journal.Record(toggle.SetEnabled(Item(), enabled: false, journal));

        toggle.SetEnabled(Item() with { State = EnableState.Disabled }, enabled: true, journal);

        Assert.Equal(@"C:\evil.exe", registry.OpenKey(Key)!.GetValue("Debugger"));
    }

    [Fact]
    public void ItemsWithNoValueNameExtraFallBackToTheItemName()
    {
        var registry = new FakeRegistry();
        registry.Set(Key, "Plain", "x", RegistryValueType.String);
        var toggle = new SubValueToggle("ifeo", registry, new FakeClock());
        var item = Item() with { Name = "Plain", Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) };

        toggle.SetEnabled(item, enabled: false, new FakeJournal());

        Assert.Null(registry.OpenKey(Key)!.GetValue("Plain"));
    }

    [Fact]
    public void CapabilityIsBackupRemove()
    {
        var toggle = new SubValueToggle("ifeo", new FakeRegistry(), new FakeClock());
        Assert.Equal(ToggleCapability.BackupRemove, toggle.GetCapability(Item()));
        Assert.Equal("ifeo", toggle.ProviderId);
    }
}
