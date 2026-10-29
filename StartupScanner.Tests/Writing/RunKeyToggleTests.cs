using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Writing;

public class RunKeyToggleTests
{
    private const string HkcuRun = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    private const string HkcuApproved = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string HklmRun = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string HklmApproved32 = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private static readonly DateTime Now = new(2026, 7, 30, 9, 12, 0, DateTimeKind.Utc);

    private static StartupItem Item(string location = HkcuRun, string name = "Spotify",
                                    string approvedRoot = "HKCU", string approvedSubKey = "Run",
                                    string view = "Default") => new()
    {
        Id = Identity.Compute("registry.run", null, location, name),
        ProviderId = "registry.run", Location = location, Name = name,
        RawValue = @"C:\Spotify.exe", Category = StartupCategory.RunKey, State = EnableState.Enabled,
        Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ApprovedRoot"] = approvedRoot, ["ApprovedSubKey"] = approvedSubKey, ["View"] = view,
        },
    };

    private static (RunKeyToggle Toggle, FakeRegistry Registry, FakeJournal Journal) Sut()
    {
        var registry = new FakeRegistry();
        return (new RunKeyToggle(registry, new FakeClock(Now)), registry, new FakeJournal());
    }

    [Fact]
    public void ProviderIdMatchesTheScanProvider()
    {
        var (toggle, _, _) = Sut();
        Assert.Equal("registry.run", toggle.ProviderId);
    }

    [Fact]
    public void CapabilityIsNativeWhenStartupApprovedCoversTheKey()
    {
        var (toggle, _, _) = Sut();
        Assert.Equal(ToggleCapability.Native, toggle.GetCapability(Item()));
    }

    [Fact]
    public void CapabilityFallsBackToBackupRemoveForKeysWindowsDoesNotTrack()
    {
        var (toggle, _, _) = Sut();
        Assert.Equal(ToggleCapability.BackupRemove, toggle.GetCapability(Item(approvedSubKey: "")));
    }

    [Fact]
    public void DisableWritesADisabledBlobIntoTheStartupApprovedKey()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(HkcuRun, "Spotify", @"C:\Spotify.exe", RegistryValueType.String);

        var record = toggle.SetEnabled(Item(), enabled: false, journal);

        var blob = Assert.IsType<byte[]>(registry.OpenKey(HkcuApproved)!.GetValue("Spotify"));
        Assert.False(StartupApprovedBlob.IsEnabled(blob));
        Assert.Equal(Now, StartupApprovedBlob.DisabledAtUtc(blob));
        Assert.Equal("StartupApproved", record.Mechanism);
        Assert.Equal(HkcuApproved, record.Location);
        Assert.Equal("Spotify", record.Name);
        Assert.False(record.Enabled);
    }

    [Fact]
    public void DisableLeavesTheRunValueItselfAlone()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(HkcuRun, "Spotify", @"C:\Spotify.exe", RegistryValueType.String);

        toggle.SetEnabled(Item(), enabled: false, journal);

        Assert.Equal(@"C:\Spotify.exe", registry.OpenKey(HkcuRun)!.GetValue("Spotify"));
    }

    [Fact]
    public void DisableRecordsNoBeforeValueWhenNoBlobExistedYet()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(HkcuRun, "Spotify", @"C:\Spotify.exe", RegistryValueType.String);

        Assert.Null(toggle.SetEnabled(Item(), enabled: false, journal).Before);
    }

    [Fact]
    public void EnableWritesAnEnabledBlobAndZeroesTheTimestamp()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(HkcuRun, "Spotify", @"C:\Spotify.exe", RegistryValueType.String);
        journal.Record(toggle.SetEnabled(Item(), enabled: false, journal));

        toggle.SetEnabled(Item() with { State = EnableState.Disabled }, enabled: true, journal);

        var blob = (byte[])registry.OpenKey(HkcuApproved)!.GetValue("Spotify")!;
        Assert.True(StartupApprovedBlob.IsEnabled(blob));
        Assert.Null(StartupApprovedBlob.DisabledAtUtc(blob));
    }

    [Fact]
    public void EnablePreservesTheOpaqueBlobBytes()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(HkcuRun, "Spotify", @"C:\Spotify.exe", RegistryValueType.String);
        registry.Set(HkcuApproved, "Spotify",
            new byte[] { 0x03, 0xAA, 0xBB, 0xCC, 1, 2, 3, 4, 5, 6, 7, 8 }, RegistryValueType.Binary);

        toggle.SetEnabled(Item() with { State = EnableState.Disabled }, enabled: true, journal);

        var blob = (byte[])registry.OpenKey(HkcuApproved)!.GetValue("Spotify")!;
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, blob[1..4]);
    }

    [Fact]
    public void ThirtyTwoBitRunKeyWritesTheRun32BlobInTheThirtyTwoBitView()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(HklmRun, "Legacy", "x.exe", RegistryValueType.String, RegistryViewKind.Registry32);

        var record = toggle.SetEnabled(
            Item(HklmRun, "Legacy", "HKLM", "Run32", "Registry32"), enabled: false, journal);

        Assert.NotNull(registry.OpenKey(HklmApproved32, RegistryViewKind.Registry32)!.GetValue("Legacy"));
        Assert.Equal("Registry32", record.View);
    }

    [Fact]
    public void RestorePutsTheEarlierBlobBack()
    {
        var (toggle, registry, journal) = Sut();
        var original = StartupApprovedBlob.Encode(true, Now, null);
        registry.Set(HkcuRun, "Spotify", @"C:\Spotify.exe", RegistryValueType.String);
        registry.Set(HkcuApproved, "Spotify", original, RegistryValueType.Binary);

        var record = toggle.SetEnabled(Item(), enabled: false, journal);
        toggle.Restore(record);

        Assert.Equal(original, (byte[])registry.OpenKey(HkcuApproved)!.GetValue("Spotify")!);
    }

    [Fact]
    public void RestoreRemovesTheBlobWhenThereWasNotOneBefore()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(HkcuRun, "Spotify", @"C:\Spotify.exe", RegistryValueType.String);

        var record = toggle.SetEnabled(Item(), enabled: false, journal);
        toggle.Restore(record);

        Assert.Null(registry.OpenKey(HkcuApproved)!.GetValue("Spotify"));
    }

    [Fact]
    public void UntrackedKeysGoThroughBackupRemoveInstead()
    {
        var (toggle, registry, journal) = Sut();
        var runOnce = @"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce";
        registry.Set(runOnce, "Once", "setup.exe", RegistryValueType.String);

        var record = toggle.SetEnabled(Item(runOnce, "Once", "HKCU", "", "Default"), enabled: false, journal);

        Assert.Equal("BackupRemove", record.Mechanism);
        Assert.Null(registry.OpenKey(runOnce)!.GetValue("Once"));
        Assert.Equal("sz:setup.exe", record.Before);
    }

    [Fact]
    public void RestoreRoutesByMechanism()
    {
        var (toggle, registry, journal) = Sut();
        var runOnce = @"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce";
        registry.Set(runOnce, "Once", "setup.exe", RegistryValueType.String);
        var record = toggle.SetEnabled(Item(runOnce, "Once", "HKCU", "", "Default"), enabled: false, journal);

        toggle.Restore(record);

        Assert.Equal("setup.exe", registry.OpenKey(runOnce)!.GetValue("Once"));
    }
}
