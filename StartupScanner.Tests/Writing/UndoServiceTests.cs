using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Storage;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Writing;

public class UndoServiceTests
{
    private const string Session = "session-1";

    private static WriteRecord Record(string itemId, bool enabled, DateTime ts, string session = Session) => new()
    {
        Ts = ts, SessionId = session, ItemId = itemId, ProviderId = "registry.run",
        Mechanism = "StartupApproved", Location = @"HKCU\Approved", Name = "Spotify",
        Category = StartupCategory.RunKey,
        Before = "binary:AgAAAAAAAAAAAAAA", After = "binary:AwAAAAAAAAAAAAAA", Enabled = enabled,
    };

    private static StartupItem Item(string itemId, EnableState state) => new()
    {
        Id = itemId, ProviderId = "registry.run", Location = @"HKCU\Run", Name = "Spotify",
        RawValue = @"C:\Spotify.exe", Category = StartupCategory.RunKey, State = state,
    };

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    private static (UndoService Service, StubToggleProvider Toggle, StubScanProvider Scanner, FakeJournal Journal) Sut()
    {
        var toggle = new StubToggleProvider("registry.run", ToggleCapability.Native);
        var scanner = new StubScanProvider("registry.run");
        var journal = new FakeJournal();
        var service = new UndoService(new IToggleProvider[] { toggle }, new IStartupProvider[] { scanner },
                                      journal, new FakeClock(), Session);
        return (service, toggle, scanner, journal);
    }

    [Fact]
    public void UndoLastRestoresTheMostRecentWrite()
    {
        var (service, toggle, scanner, journal) = Sut();
        var record = Record("id-1", enabled: false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc));
        journal.Record(record);
        // Restoring the pre-write blob puts the entry back to Enabled.
        scanner.Items.Add(Item("id-1", EnableState.Enabled));

        var result = service.UndoLast(Ctx());

        Assert.True(result.Success);
        Assert.Equal(record, toggle.Restored.Single());
    }

    [Fact]
    public void UndoAppendsAnInverseRecordSoTheJournalStaysAppendOnly()
    {
        var (service, _, scanner, journal) = Sut();
        journal.Record(Record("id-1", enabled: false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc)));
        scanner.Items.Add(Item("id-1", EnableState.Enabled));

        service.UndoLast(Ctx());

        Assert.Equal(2, journal.All.Count);
        var inverse = journal.All[^1];
        Assert.Equal("binary:AwAAAAAAAAAAAAAA", inverse.Before);
        Assert.Equal("binary:AgAAAAAAAAAAAAAA", inverse.After);
        Assert.True(inverse.Enabled);
        Assert.Equal("session-1:undo", inverse.SessionId);
    }

    [Fact]
    public void UndoingTwiceStepsFurtherBackRatherThanRedoing()
    {
        var (service, toggle, scanner, journal) = Sut();
        var first = Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc));
        var second = Record("id-2", false, new DateTime(2026, 7, 30, 9, 5, 0, DateTimeKind.Utc));
        journal.Record(first);
        journal.Record(second);
        scanner.Items.Add(Item("id-1", EnableState.Enabled));
        scanner.Items.Add(Item("id-2", EnableState.Enabled));

        service.UndoLast(Ctx());
        service.UndoLast(Ctx());

        Assert.Equal(new[] { second, first }, toggle.Restored);
    }

    [Fact]
    public void UndoLastReportsWhenThereIsNothingLeft()
    {
        var (service, _, _, _) = Sut();

        var result = service.UndoLast(Ctx());

        Assert.False(result.Success);
        Assert.Contains("nothing", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UndoSessionReversesEveryWriteFromThisSessionNewestFirst()
    {
        var (service, toggle, scanner, journal) = Sut();
        var first = Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc));
        var second = Record("id-2", false, new DateTime(2026, 7, 30, 9, 5, 0, DateTimeKind.Utc));
        journal.Record(first);
        journal.Record(second);
        scanner.Items.Add(Item("id-1", EnableState.Enabled));
        scanner.Items.Add(Item("id-2", EnableState.Enabled));

        var results = service.UndoSession(Ctx());

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal(new[] { second, first }, toggle.Restored);
    }

    [Fact]
    public void UndoSessionIgnoresWritesFromEarlierSessions()
    {
        var (service, toggle, scanner, journal) = Sut();
        journal.Record(Record("id-old", false, new DateTime(2026, 7, 28, 9, 0, 0, DateTimeKind.Utc), "older-session"));
        var mine = Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc));
        journal.Record(mine);
        scanner.Items.Add(Item("id-1", EnableState.Enabled));

        service.UndoSession(Ctx());

        Assert.Equal(mine, toggle.Restored.Single());
    }

    [Fact]
    public void RunningUndoSessionTwiceDoesNothingTheSecondTime()
    {
        var (service, toggle, scanner, journal) = Sut();
        journal.Record(Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc)));
        scanner.Items.Add(Item("id-1", EnableState.Enabled));

        service.UndoSession(Ctx());
        var second = service.UndoSession(Ctx());

        Assert.Empty(second);
        Assert.Single(toggle.Restored);
    }

    [Fact]
    public void AFailedVerificationDuringUndoIsReportedAndNothingIsJournalled()
    {
        var (service, _, scanner, journal) = Sut();
        journal.Record(Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc)));
        // The read-back still says Disabled, so the restore did not land.
        scanner.Items.Add(Item("id-1", EnableState.Disabled));

        var result = service.UndoLast(Ctx());

        Assert.False(result.Success);
        Assert.Single(journal.All);
    }

    [Fact]
    public void UndoSessionStopsAtTheFirstFailure()
    {
        var (service, toggle, scanner, journal) = Sut();
        journal.Record(Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc)));
        journal.Record(Record("id-2", false, new DateTime(2026, 7, 30, 9, 5, 0, DateTimeKind.Utc)));
        scanner.Items.Add(Item("id-2", EnableState.Disabled));   // id-2 will fail verification

        var results = service.UndoSession(Ctx());

        Assert.Single(results);
        Assert.False(results[0].Success);
        Assert.Single(toggle.Restored);
    }
}
