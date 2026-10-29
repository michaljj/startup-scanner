using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Storage;

public class HistoryStoreTests
{
    private static readonly AppPaths Paths = new(@"C:\Data\StartupScanner");

    private static DiffEvent Event(DiffEventKind kind, string id, DateTime ts) => new()
    {
        Ts = ts, Event = kind, Id = id, Name = "Thing",
        ProviderId = "registry.run", Location = @"HKCU\Run",
        Before = kind == DiffEventKind.Added ? null : "old",
        After = kind == DiffEventKind.Removed ? null : "new",
    };

    [Fact]
    public void AppendsOneJsonObjectPerLine()
    {
        var fs = new FakeFileSystem();
        var store = new HistoryStore(fs, Paths);

        store.Append(new[]
        {
            Event(DiffEventKind.Added, "id-1", new DateTime(2026, 7, 28, 9, 0, 0, DateTimeKind.Utc)),
            Event(DiffEventKind.Modified, "id-1", new DateTime(2026, 7, 29, 9, 0, 0, DateTimeKind.Utc)),
        });

        var lines = fs.ReadAllText(Paths.HistoryFile).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("{", l));
    }

    [Fact]
    public void AppendIsAdditiveAcrossCalls()
    {
        var fs = new FakeFileSystem();
        var store = new HistoryStore(fs, Paths);

        store.Append(new[] { Event(DiffEventKind.Added, "id-1", new DateTime(2026, 7, 28, 9, 0, 0, DateTimeKind.Utc)) });
        store.Append(new[] { Event(DiffEventKind.Removed, "id-1", new DateTime(2026, 7, 29, 9, 0, 0, DateTimeKind.Utc)) });

        Assert.Equal(2, store.ReadFor("id-1").Count);
    }

    [Fact]
    public void ReadForFiltersByItemIdAndPreservesOrder()
    {
        var fs = new FakeFileSystem();
        var store = new HistoryStore(fs, Paths);

        store.Append(new[]
        {
            Event(DiffEventKind.Added, "id-1", new DateTime(2026, 7, 28, 9, 0, 0, DateTimeKind.Utc)),
            Event(DiffEventKind.Added, "id-2", new DateTime(2026, 7, 28, 9, 0, 1, DateTimeKind.Utc)),
            Event(DiffEventKind.StateChanged, "id-1", new DateTime(2026, 7, 29, 9, 0, 0, DateTimeKind.Utc)),
        });

        var events = store.ReadFor("id-1");

        Assert.Equal(2, events.Count);
        Assert.Equal(DiffEventKind.Added, events[0].Event);
        Assert.Equal(DiffEventKind.StateChanged, events[1].Event);
    }

    [Fact]
    public void FirstSeenIsTheEarliestAddedEvent()
    {
        var fs = new FakeFileSystem();
        var store = new HistoryStore(fs, Paths);
        var first = new DateTime(2026, 7, 20, 8, 0, 0, DateTimeKind.Utc);

        store.Append(new[]
        {
            Event(DiffEventKind.Added, "id-1", first),
            Event(DiffEventKind.Removed, "id-1", first.AddDays(1)),
            Event(DiffEventKind.Added, "id-1", first.AddDays(2)),
        });

        Assert.Equal(first, store.FirstSeenUtc("id-1"));
    }

    [Fact]
    public void MissingFileReadsAsEmpty()
    {
        var store = new HistoryStore(new FakeFileSystem(), Paths);
        Assert.Empty(store.ReadFor("id-1"));
        Assert.Null(store.FirstSeenUtc("id-1"));
    }

    [Fact]
    public void UnparseableLinesAreSkipped()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Paths.HistoryFile, "not json\n");
        var store = new HistoryStore(fs, Paths);

        store.Append(new[] { Event(DiffEventKind.Added, "id-1", new DateTime(2026, 7, 28, 9, 0, 0, DateTimeKind.Utc)) });

        Assert.Single(store.ReadFor("id-1"));
    }
}
