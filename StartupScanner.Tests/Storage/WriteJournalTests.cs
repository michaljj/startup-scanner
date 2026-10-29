using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Storage;

public class WriteJournalTests
{
    private static readonly AppPaths Paths = new(@"C:\Data\StartupScanner");

    private static WriteRecord Record(string itemId, bool enabled, DateTime ts, string session = "s1") => new()
    {
        Ts = ts, SessionId = session, ItemId = itemId, ProviderId = "registry.run",
        Mechanism = "StartupApproved", Location = @"HKCU\Run", Name = "Thing",
        Category = StartupCategory.RunKey, UserSid = null,
        Before = "binary:AgAAAAAAAAAAAAAA", After = "binary:AwAAAAAAAAAAAAAA", Enabled = enabled,
    };

    [Fact]
    public void RecordAppendsOneLinePerWrite()
    {
        var fs = new FakeFileSystem();
        var journal = new WriteJournal(fs, Paths);

        journal.Record(Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc)));
        journal.Record(Record("id-1", true, new DateTime(2026, 7, 30, 9, 5, 0, DateTimeKind.Utc)));

        var lines = fs.ReadAllText(Paths.WritesFile).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void GetLastReturnsTheMostRecentWriteForThatItem()
    {
        var fs = new FakeFileSystem();
        var journal = new WriteJournal(fs, Paths);

        journal.Record(Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc)));
        journal.Record(Record("id-1", true, new DateTime(2026, 7, 30, 9, 5, 0, DateTimeKind.Utc)));
        journal.Record(Record("id-2", false, new DateTime(2026, 7, 30, 9, 6, 0, DateTimeKind.Utc)));

        Assert.True(journal.GetLast("id-1")!.Enabled);
        Assert.False(journal.GetLast("id-2")!.Enabled);
        Assert.Null(journal.GetLast("id-3"));
    }

    [Fact]
    public void ExistingFileIsLoadedIntoTheLastWriteWinsIndexAtConstruction()
    {
        var fs = new FakeFileSystem();
        new WriteJournal(fs, Paths).Record(Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc)));

        var reloaded = new WriteJournal(fs, Paths);

        Assert.Single(reloaded.All);
        Assert.False(reloaded.GetLast("id-1")!.Enabled);
    }

    [Fact]
    public void AllPreservesAppendOrder()
    {
        var fs = new FakeFileSystem();
        var journal = new WriteJournal(fs, Paths);

        journal.Record(Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc)));
        journal.Record(Record("id-2", false, new DateTime(2026, 7, 30, 9, 1, 0, DateTimeKind.Utc)));

        Assert.Equal(new[] { "id-1", "id-2" }, journal.All.Select(r => r.ItemId).ToArray());
    }

    [Fact]
    public void ForSessionFiltersBySessionId()
    {
        var fs = new FakeFileSystem();
        var journal = new WriteJournal(fs, Paths);

        journal.Record(Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc), "old-session"));
        journal.Record(Record("id-2", false, new DateTime(2026, 7, 30, 9, 1, 0, DateTimeKind.Utc), "this-session"));

        Assert.Equal(new[] { "id-2" }, journal.ForSession("this-session").Select(r => r.ItemId).ToArray());
    }

    [Fact]
    public void MechanismAndCategorySurviveTheRoundTrip()
    {
        var fs = new FakeFileSystem();
        new WriteJournal(fs, Paths).Record(Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc))
            with { Mechanism = "BackupRemove", Category = StartupCategory.Winlogon, UserSid = "S-1-5-21-3" });

        var record = new WriteJournal(fs, Paths).GetLast("id-1")!;

        Assert.Equal("BackupRemove", record.Mechanism);
        Assert.Equal(StartupCategory.Winlogon, record.Category);
        Assert.Equal("S-1-5-21-3", record.UserSid);
    }

    [Fact]
    public void CorruptLinesAreSkippedWithoutLosingGoodOnes()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Paths.WritesFile, "half-written line\n");

        var journal = new WriteJournal(fs, Paths);
        journal.Record(Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc)));

        Assert.Single(new WriteJournal(fs, Paths).All);
    }
}
