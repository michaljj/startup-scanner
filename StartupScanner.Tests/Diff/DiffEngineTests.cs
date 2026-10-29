using StartupScanner.Core.Diff;
using StartupScanner.Core.Models;

namespace StartupScanner.Tests.Diff;

public class DiffEngineTests
{
    private static readonly DateTime Ts = new(2026, 7, 30, 9, 12, 0, DateTimeKind.Utc);

    private static StartupItem Item(string name, string raw = "cmd", EnableState state = EnableState.Enabled) => new()
    {
        Id = Identity.Compute("registry.run", null, @"HKCU\Run", name),
        ProviderId = "registry.run",
        Location = @"HKCU\Run",
        Name = name,
        RawValue = raw,
        Category = StartupCategory.RunKey,
        State = state,
    };

    [Fact]
    public void ReportsAddedWhenAnIdIsOnlyInTheCurrentScan()
    {
        var events = DiffEngine.Compare(Array.Empty<StartupItem>(), new[] { Item("New") }, Ts);

        var e = Assert.Single(events);
        Assert.Equal(DiffEventKind.Added, e.Event);
        Assert.Equal("New", e.Name);
        Assert.Null(e.Before);
        Assert.Equal("cmd", e.After);
        Assert.Equal(Ts, e.Ts);
    }

    [Fact]
    public void ReportsRemovedWhenAnIdIsOnlyInTheSnapshot()
    {
        var events = DiffEngine.Compare(new[] { Item("Gone") }, Array.Empty<StartupItem>(), Ts);

        var e = Assert.Single(events);
        Assert.Equal(DiffEventKind.Removed, e.Event);
        Assert.Equal("cmd", e.Before);
        Assert.Null(e.After);
    }

    [Fact]
    public void ReportsModifiedWhenOnlyRawValueChanged()
    {
        var events = DiffEngine.Compare(new[] { Item("X", "old.exe") }, new[] { Item("X", "new.exe") }, Ts);

        var e = Assert.Single(events);
        Assert.Equal(DiffEventKind.Modified, e.Event);
        Assert.Equal("old.exe", e.Before);
        Assert.Equal("new.exe", e.After);
    }

    [Fact]
    public void ReportsStateChangedWhenSomethingElseReEnabledTheEntry()
    {
        var events = DiffEngine.Compare(
            new[] { Item("X", "cmd", EnableState.Disabled) },
            new[] { Item("X", "cmd", EnableState.Enabled) }, Ts);

        var e = Assert.Single(events);
        Assert.Equal(DiffEventKind.StateChanged, e.Event);
        Assert.Equal("Disabled", e.Before);
        Assert.Equal("Enabled", e.After);
    }

    [Fact]
    public void EmitsBothModifiedAndStateChangedWhenBothChanged()
    {
        var events = DiffEngine.Compare(
            new[] { Item("X", "old.exe", EnableState.Disabled) },
            new[] { Item("X", "new.exe", EnableState.Enabled) }, Ts);

        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.Event == DiffEventKind.Modified);
        Assert.Contains(events, e => e.Event == DiffEventKind.StateChanged);
    }

    [Fact]
    public void AChangedCommandLineIsNotReportedAsRemovedPlusAdded()
    {
        var events = DiffEngine.Compare(new[] { Item("X", "old.exe") }, new[] { Item("X", "new.exe") }, Ts);

        Assert.DoesNotContain(events, e => e.Event is DiffEventKind.Added or DiffEventKind.Removed);
    }

    [Fact]
    public void IdenticalScansProduceNoEvents()
    {
        Assert.Empty(DiffEngine.Compare(new[] { Item("X") }, new[] { Item("X") }, Ts));
    }

    [Fact]
    public void EventsCarryProviderAndLocationForRendering()
    {
        var e = Assert.Single(DiffEngine.Compare(Array.Empty<StartupItem>(), new[] { Item("New") }, Ts));

        Assert.Equal("registry.run", e.ProviderId);
        Assert.Equal(@"HKCU\Run", e.Location);
    }
}
