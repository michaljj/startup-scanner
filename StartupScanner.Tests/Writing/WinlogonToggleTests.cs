using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Writing;

public class WinlogonToggleTests
{
    private const string Machine = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

    private static StartupItem Item(string name, string raw) => new()
    {
        Id = Identity.Compute("winlogon", null, Machine, name),
        ProviderId = "winlogon", Location = Machine, Name = name, RawValue = raw,
        Category = StartupCategory.Winlogon, State = EnableState.Enabled,
    };

    [Fact]
    public void DisablingAHijackedUserinitWritesTheOsDefaultBackInsteadOfDeleting()
    {
        var registry = new FakeRegistry();
        registry.Set(Machine, "Userinit", @"C:\Windows\system32\userinit.exe,C:\evil.exe", RegistryValueType.ExpandString);
        var toggle = WinlogonToggle.Create(registry, new FakeClock());

        var record = toggle.SetEnabled(Item("Userinit", @"C:\Windows\system32\userinit.exe,C:\evil.exe"),
                                      enabled: false, new FakeJournal());

        Assert.Equal(@"%systemroot%\system32\userinit.exe,", registry.OpenKey(Machine)!.GetValue("Userinit"));
        Assert.Equal(@"expand:C:\Windows\system32\userinit.exe,C:\evil.exe", record.Before);
    }

    [Fact]
    public void DisablingAHijackedShellWritesExplorerBack()
    {
        var registry = new FakeRegistry();
        registry.Set(Machine, "Shell", "explorer.exe,evil.exe", RegistryValueType.String);
        var toggle = WinlogonToggle.Create(registry, new FakeClock());

        toggle.SetEnabled(Item("Shell", "explorer.exe,evil.exe"), enabled: false, new FakeJournal());

        Assert.Equal("explorer.exe", registry.OpenKey(Machine)!.GetValue("Shell"));
    }

    [Fact]
    public void DisablingAnyOtherWinlogonValueDeletesIt()
    {
        var registry = new FakeRegistry();
        registry.Set(Machine, "Taskman", @"C:\taskman.exe", RegistryValueType.String);
        var toggle = WinlogonToggle.Create(registry, new FakeClock());

        toggle.SetEnabled(Item("Taskman", @"C:\taskman.exe"), enabled: false, new FakeJournal());

        Assert.Null(registry.OpenKey(Machine)!.GetValue("Taskman"));
    }

    [Fact]
    public void ReEnablingRestoresTheExactPriorValue()
    {
        var registry = new FakeRegistry();
        registry.Set(Machine, "Userinit", @"C:\Windows\system32\userinit.exe,C:\evil.exe", RegistryValueType.ExpandString);
        var toggle = WinlogonToggle.Create(registry, new FakeClock());
        var journal = new FakeJournal();
        var item = Item("Userinit", @"C:\Windows\system32\userinit.exe,C:\evil.exe");
        journal.Record(toggle.SetEnabled(item, enabled: false, journal));

        toggle.SetEnabled(item with { State = EnableState.Disabled }, enabled: true, journal);

        Assert.Equal(@"C:\Windows\system32\userinit.exe,C:\evil.exe", registry.OpenKey(Machine)!.GetValue("Userinit"));
    }
}
