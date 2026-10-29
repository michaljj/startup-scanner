using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Writing;

public class RegistryValueToggleTests
{
    private const string Winlogon = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

    private static StartupItem Item(string name = "Taskman", string raw = @"C:\taskman.exe",
                                    StartupCategory category = StartupCategory.Winlogon,
                                    string? view = null) => new()
    {
        Id = Identity.Compute("winlogon", null, Winlogon, name),
        ProviderId = "winlogon", Location = Winlogon, Name = name, RawValue = raw, Category = category,
        State = EnableState.Enabled,
        Extra = view is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["View"] = view },
    };

    private static (RegistryValueToggle Toggle, FakeRegistry Registry, FakeJournal Journal) Sut(
        Func<StartupItem, (object, RegistryValueType)?>? replacement = null)
    {
        var registry = new FakeRegistry();
        return (new RegistryValueToggle("winlogon", registry, new FakeClock(), replacement), registry, new FakeJournal());
    }

    [Fact]
    public void CapabilityIsBackupRemoveForAnOrdinaryEntry()
    {
        var (toggle, _, _) = Sut();
        Assert.Equal(ToggleCapability.BackupRemove, toggle.GetCapability(Item()));
        Assert.Equal("winlogon", toggle.ProviderId);
    }

    [Fact]
    public void CapabilityIsBlockedForAGuardrailedEntry()
    {
        var (toggle, _, _) = Sut();
        Assert.Equal(ToggleCapability.Blocked, toggle.GetCapability(Item("Shell", "explorer.exe")));
    }

    [Fact]
    public void DisableJournalsTheCurrentValueAndDeletesIt()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(Winlogon, "Taskman", @"C:\taskman.exe", RegistryValueType.String);

        var record = toggle.SetEnabled(Item(), enabled: false, journal);

        Assert.Null(registry.OpenKey(Winlogon)!.GetValue("Taskman"));
        Assert.Equal("BackupRemove", record.Mechanism);
        Assert.Equal(@"sz:C:\taskman.exe", record.Before);
        Assert.Null(record.After);
        Assert.False(record.Enabled);
        Assert.Equal(Winlogon, record.Location);
        Assert.Equal("Taskman", record.Name);
        Assert.Equal(StartupCategory.Winlogon, record.Category);
    }

    [Fact]
    public void DisablePreservesTheValueTypeInTheJournal()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(Winlogon, "Multi", new[] { "a", "b" }, RegistryValueType.MultiString);

        var record = toggle.SetEnabled(Item("Multi", "a; b"), enabled: false, journal);

        Assert.Equal("multi:[\"a\",\"b\"]", record.Before);
    }

    [Fact]
    public void DisableWithAReplacementWritesTheReplacementInsteadOfDeleting()
    {
        var (toggle, registry, journal) = Sut(_ => (@"%systemroot%\system32\userinit.exe,", RegistryValueType.ExpandString));
        registry.Set(Winlogon, "Userinit", @"C:\Windows\system32\userinit.exe,C:\evil.exe", RegistryValueType.ExpandString);

        var record = toggle.SetEnabled(Item("Userinit", @"C:\Windows\system32\userinit.exe,C:\evil.exe"), false, journal);

        Assert.Equal(@"%systemroot%\system32\userinit.exe,", registry.OpenKey(Winlogon)!.GetValue("Userinit"));
        Assert.Equal(@"expand:C:\Windows\system32\userinit.exe,C:\evil.exe", record.Before);
        Assert.Equal(@"expand:%systemroot%\system32\userinit.exe,", record.After);
    }

    [Fact]
    public void EnableRestoresTheValueFromTheJournal()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(Winlogon, "Taskman", @"C:\taskman.exe", RegistryValueType.String);
        var disable = toggle.SetEnabled(Item(), enabled: false, journal);
        journal.Record(disable);

        var record = toggle.SetEnabled(Item() with { State = EnableState.Disabled }, enabled: true, journal);

        Assert.Equal(@"C:\taskman.exe", registry.OpenKey(Winlogon)!.GetValue("Taskman"));
        Assert.Null(record.Before);
        Assert.Equal(@"sz:C:\taskman.exe", record.After);
        Assert.True(record.Enabled);
    }

    [Fact]
    public void EnableWithNoJournalledBackupThrowsRatherThanGuessing()
    {
        var (toggle, _, journal) = Sut();

        var ex = Assert.Throws<InvalidOperationException>(
            () => toggle.SetEnabled(Item(), enabled: true, journal));

        Assert.Contains("Taskman", ex.Message);
    }

    [Fact]
    public void EnableUsesTheMostRecentDisableWhenAnItemWasToggledRepeatedly()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(Winlogon, "Taskman", "first.exe", RegistryValueType.String);
        journal.Record(toggle.SetEnabled(Item(), false, journal));
        journal.Record(toggle.SetEnabled(Item() with { State = EnableState.Disabled }, true, journal));
        registry.Set(Winlogon, "Taskman", "second.exe", RegistryValueType.String);
        journal.Record(toggle.SetEnabled(Item(), false, journal));

        toggle.SetEnabled(Item() with { State = EnableState.Disabled }, true, journal);

        Assert.Equal("second.exe", registry.OpenKey(Winlogon)!.GetValue("Taskman"));
    }

    [Fact]
    public void RestorePutsBackTheBeforeValue()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(Winlogon, "Taskman", @"C:\taskman.exe", RegistryValueType.String);
        var record = toggle.SetEnabled(Item(), enabled: false, journal);

        toggle.Restore(record);

        Assert.Equal(@"C:\taskman.exe", registry.OpenKey(Winlogon)!.GetValue("Taskman"));
    }

    [Fact]
    public void RestoreOfAnEnableRecordDeletesTheValueAgain()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(Winlogon, "Taskman", @"C:\taskman.exe", RegistryValueType.String);
        journal.Record(toggle.SetEnabled(Item(), false, journal));
        var enable = toggle.SetEnabled(Item() with { State = EnableState.Disabled }, true, journal);

        toggle.Restore(enable);

        Assert.Null(registry.OpenKey(Winlogon)!.GetValue("Taskman"));
    }

    [Fact]
    public void ViewIsCarriedIntoTheRecordAndHonouredOnRestore()
    {
        var registry = new FakeRegistry();
        var toggle = new RegistryValueToggle("winlogon", registry, new FakeClock());
        var journal = new FakeJournal();
        registry.Set(Winlogon, "Taskman", "x.exe", RegistryValueType.String, RegistryViewKind.Registry32);

        var record = toggle.SetEnabled(Item(view: "Registry32"), enabled: false, journal);
        toggle.Restore(record);

        Assert.Equal("Registry32", record.View);
        Assert.Equal("x.exe", registry.OpenKey(Winlogon, RegistryViewKind.Registry32)!.GetValue("Taskman"));
    }

    [Fact]
    public void DisableOnAMissingKeyThrowsAndWritesNothing()
    {
        var (toggle, _, journal) = Sut();

        Assert.Throws<InvalidOperationException>(() => toggle.SetEnabled(Item(), enabled: false, journal));
        Assert.Empty(journal.All);
    }
}
