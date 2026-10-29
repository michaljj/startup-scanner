using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Storage;

public class SnapshotStoreTests
{
    private static readonly AppPaths Paths = new(@"C:\Data\StartupScanner");
    private static readonly DateTime Scanned = new(2026, 7, 30, 9, 12, 0, DateTimeKind.Utc);

    private static StartupItem Item(string name = "OneDrive") => new()
    {
        Id = Identity.Compute("registry.run", null, @"HKCU\Run", name),
        ProviderId = "registry.run",
        Location = @"HKCU\Run",
        Name = name,
        RawValue = @"C:\OneDrive.exe /background",
        Category = StartupCategory.RunKey,
        ImagePath = @"C:\OneDrive.exe",
        Arguments = "/background",
        State = EnableState.Enabled,
        StateSource = "StartupApproved",
        Risk = RiskTier.Low,
        UserSid = "S-1-5-21-9",
        Extra = new Dictionary<string, string> { ["Start"] = "2" },
    };

    [Fact]
    public void LoadReturnsNullWhenNoSnapshotExists()
    {
        Assert.Null(new SnapshotStore(new FakeFileSystem(), Paths).Load());
    }

    [Fact]
    public void RoundTripsItemsAndTimestamp()
    {
        var fs = new FakeFileSystem();
        var store = new SnapshotStore(fs, Paths);

        store.Save(new[] { Item() }, Scanned);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(Scanned, loaded!.ScannedAtUtc);
        var item = Assert.Single(loaded.Items);
        Assert.Equal(Item().Id, item.Id);
        Assert.Equal(@"C:\OneDrive.exe /background", item.RawValue);
        Assert.Equal(EnableState.Enabled, item.State);
        Assert.Equal(RiskTier.Low, item.Risk);
        Assert.Equal("S-1-5-21-9", item.UserSid);
        Assert.Equal("2", item.Extra["Start"]);
    }

    [Fact]
    public void DoesNotPersistLazilyComputedFileFacts()
    {
        var fs = new FakeFileSystem();
        var store = new SnapshotStore(fs, Paths);

        store.Save(new[] { Item() with { File = new FileFacts { Path = @"C:\OneDrive.exe", Exists = true, Sha256 = "abc" } } }, Scanned);

        Assert.DoesNotContain("abc", fs.ReadAllText(Paths.SnapshotFile));
        Assert.Null(store.Load()!.Items[0].File);
    }

    [Fact]
    public void SaveOverwritesThePreviousSnapshotAtomically()
    {
        var fs = new FakeFileSystem();
        var store = new SnapshotStore(fs, Paths);

        store.Save(new[] { Item("A") }, Scanned);
        store.Save(new[] { Item("B") }, Scanned.AddMinutes(5));

        Assert.Equal("B", Assert.Single(store.Load()!.Items).Name);
        Assert.False(fs.FileExists(Paths.SnapshotFile + ".tmp"));
    }

    [Fact]
    public void CorruptSnapshotLoadsAsNullRatherThanThrowing()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Paths.SnapshotFile, "{ this is not json");

        Assert.Null(new SnapshotStore(fs, Paths).Load());
    }
}
