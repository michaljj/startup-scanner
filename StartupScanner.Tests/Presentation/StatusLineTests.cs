using StartupScanner.Core.Models;
using StartupScanner.Core.Presentation;

namespace StartupScanner.Tests.Presentation;

public class StatusLineTests
{
    private static DiffEvent Event(DiffEventKind kind) => new()
    {
        Ts = new DateTime(2026, 7, 30, 9, 12, 0, DateTimeKind.Utc), Event = kind,
        Id = "id", Name = "n", ProviderId = "p", Location = "l",
    };

    [Fact]
    public void CountsItemsAndProviders()
    {
        var text = StatusLine.Format(412, 19, Array.Empty<DiffEvent>(), null);

        Assert.Contains("412 items", text);
        Assert.Contains("19 providers", text);
    }

    [Fact]
    public void SummarisesEachEventKindWithItsOwnCount()
    {
        var events = new[]
        {
            Event(DiffEventKind.Added), Event(DiffEventKind.Added), Event(DiffEventKind.Added),
            Event(DiffEventKind.Removed),
            Event(DiffEventKind.Modified),
            Event(DiffEventKind.StateChanged),
        };

        var text = StatusLine.Format(412, 19, events, new DateTime(2026, 7, 28, 9, 12, 0, DateTimeKind.Utc));

        Assert.Contains("3 added", text);
        Assert.Contains("1 removed", text);
        Assert.Contains("1 modified", text);
        Assert.Contains("1 re-enabled", text);
    }

    [Fact]
    public void NamesThePreviousScanTime()
    {
        var text = StatusLine.Format(1, 19, new[] { Event(DiffEventKind.Added) },
                                     new DateTime(2026, 7, 28, 9, 12, 0, DateTimeKind.Utc));

        Assert.Contains("2026-07-28 09:12", text);
    }

    [Fact]
    public void SaysThisIsTheFirstScanWhenThereIsNoBaseline()
    {
        var text = StatusLine.Format(412, 19, Array.Empty<DiffEvent>(), null);

        Assert.Contains("first scan", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SaysNoChangesWhenThereIsABaselineButNoEvents()
    {
        var text = StatusLine.Format(412, 19, Array.Empty<DiffEvent>(),
                                     new DateTime(2026, 7, 28, 9, 12, 0, DateTimeKind.Utc));

        Assert.Contains("no changes", text, StringComparison.OrdinalIgnoreCase);
    }
}
