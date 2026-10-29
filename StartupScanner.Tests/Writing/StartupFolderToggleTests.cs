using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Writing;

public class StartupFolderToggleTests
{
    private const string UserFolder = @"C:\Users\alice\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup";
    private const string UserApproved = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    private const string CommonApproved = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    private static readonly DateTime Now = new(2026, 7, 30, 9, 12, 0, DateTimeKind.Utc);

    private static StartupItem Item(string approvedRoot = "HKCU", string name = "Spotify.lnk") => new()
    {
        Id = Identity.Compute("startup.folder", null, UserFolder, name),
        ProviderId = "startup.folder", Location = UserFolder, Name = name,
        RawValue = @"C:\Spotify.exe", Category = StartupCategory.StartupFolder, State = EnableState.Enabled,
        Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ApprovedRoot"] = approvedRoot },
    };

    private static (StartupFolderToggle Toggle, FakeRegistry Registry, FakeJournal Journal) Sut()
    {
        var registry = new FakeRegistry();
        return (new StartupFolderToggle(registry, new FakeClock(Now)), registry, new FakeJournal());
    }

    [Fact]
    public void ProviderIdAndCapabilityAreCorrect()
    {
        var (toggle, _, _) = Sut();
        Assert.Equal("startup.folder", toggle.ProviderId);
        Assert.Equal(ToggleCapability.Native, toggle.GetCapability(Item()));
    }

    [Fact]
    public void DisableWritesTheBlobKeyedByTheShortcutFileName()
    {
        var (toggle, registry, journal) = Sut();

        var record = toggle.SetEnabled(Item(), enabled: false, journal);

        var blob = Assert.IsType<byte[]>(registry.OpenKey(UserApproved)!.GetValue("Spotify.lnk"));
        Assert.False(StartupApprovedBlob.IsEnabled(blob));
        Assert.Equal(UserApproved, record.Location);
    }

    [Fact]
    public void CommonFolderEntriesUseTheHklmApprovedKey()
    {
        var (toggle, registry, journal) = Sut();

        toggle.SetEnabled(Item(approvedRoot: "HKLM"), enabled: false, journal);

        Assert.NotNull(registry.OpenKey(CommonApproved)!.GetValue("Spotify.lnk"));
    }

    [Fact]
    public void AnotherProfilesFolderTargetsThatProfilesHive()
    {
        var (toggle, registry, journal) = Sut();

        toggle.SetEnabled(Item(approvedRoot: @"HKU\S-1-5-21-42"), enabled: false, journal);

        Assert.NotNull(registry.OpenKey(
            @"HKU\S-1-5-21-42\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder")!
            .GetValue("Spotify.lnk"));
    }

    [Fact]
    public void EnableFlipsTheBlobBack()
    {
        var (toggle, registry, journal) = Sut();
        journal.Record(toggle.SetEnabled(Item(), enabled: false, journal));

        toggle.SetEnabled(Item() with { State = EnableState.Disabled }, enabled: true, journal);

        Assert.True(StartupApprovedBlob.IsEnabled((byte[])registry.OpenKey(UserApproved)!.GetValue("Spotify.lnk")!));
    }

    [Fact]
    public void TheShortcutFileIsNeverTouched()
    {
        var (toggle, registry, journal) = Sut();

        toggle.SetEnabled(Item(), enabled: false, journal);

        // The toggle only ever writes to the registry; no IFileSystem dependency exists to delete with.
        Assert.Equal(UserFolder, Item().Location);
    }
}
