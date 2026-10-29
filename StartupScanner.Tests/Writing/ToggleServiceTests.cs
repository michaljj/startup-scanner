using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Storage;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Writing;

public class ToggleServiceTests
{
    private const string Session = "session-1";

    private static StartupItem Item(RiskTier risk = RiskTier.Low, EnableState state = EnableState.Enabled,
                                    string name = "Spotify", StartupCategory category = StartupCategory.RunKey,
                                    string raw = @"C:\Spotify.exe") => new()
    {
        Id = Identity.Compute("registry.run", null, @"HKCU\Run", name),
        ProviderId = "registry.run", Location = @"HKCU\Run", Name = name, RawValue = raw,
        Category = category, State = state, Risk = risk,
    };

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    private static ToggleService Build(
        StubToggleProvider toggle, StubScanProvider scanner, FakeJournal journal,
        IConfirmationSink? confirmations = null, StubRestorePointGate? gate = null)
        => new(new IToggleProvider[] { toggle }, new IStartupProvider[] { scanner }, journal,
               gate ?? new StubRestorePointGate(), confirmations ?? new RecordingConfirmationSink(),
               new FakeClock(), Session);

    [Fact]
    public void LowRiskToggleWritesWithoutAConfirmation()
    {
        var item = Item();
        var toggle = new StubToggleProvider("registry.run", ToggleCapability.Native);
        var scanner = new StubScanProvider("registry.run", item with { State = EnableState.Disabled });
        var journal = new FakeJournal();
        var confirmations = new RecordingConfirmationSink();

        var result = Build(toggle, scanner, journal, confirmations).SetEnabled(item, enabled: false, Ctx());

        Assert.True(result.Success);
        Assert.Empty(confirmations.Calls);
        Assert.Equal(Session, journal.All.Single().SessionId);
    }

    [Fact]
    public void MediumRiskAsksForConfirmationBeforeWriting()
    {
        var item = Item(RiskTier.Medium, category: StartupCategory.Service);
        var toggle = new StubToggleProvider("registry.run", ToggleCapability.Native);
        var scanner = new StubScanProvider("registry.run", item with { State = EnableState.Disabled });
        var confirmations = new RecordingConfirmationSink();

        var result = Build(toggle, scanner, new FakeJournal(), confirmations).SetEnabled(item, false, Ctx());

        Assert.True(result.Success);
        Assert.Equal(RiskTier.Medium, confirmations.Calls.Single().Tier);
    }

    [Fact]
    public void DecliningTheConfirmationCancelsAndWritesNothing()
    {
        var item = Item(RiskTier.High, category: StartupCategory.Winlogon, name: "Taskman");
        var toggle = new StubToggleProvider("registry.run", ToggleCapability.Native);
        var journal = new FakeJournal();

        var result = Build(toggle, new StubScanProvider("registry.run"), journal,
                           new RecordingConfirmationSink(answer: false)).SetEnabled(item, false, Ctx());

        Assert.True(result.Cancelled);
        Assert.Empty(journal.All);
        Assert.Empty(toggle.Restored);
    }

    [Fact]
    public void ABlockedItemIsRefusedWithTheGuardrailReason()
    {
        var item = Item(RiskTier.Critical, category: StartupCategory.Winlogon, name: "Shell", raw: "explorer.exe");
        var journal = new FakeJournal();

        var result = Build(new StubToggleProvider("registry.run", ToggleCapability.Blocked),
                           new StubScanProvider("registry.run"), journal).SetEnabled(item, false, Ctx());

        Assert.False(result.Success);
        Assert.Contains("shell", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(journal.All);
    }

    [Fact]
    public void ARestorePointIsRequestedBeforeTheFirstWrite()
    {
        var item = Item();
        var gate = new StubRestorePointGate();
        var scanner = new StubScanProvider("registry.run", item with { State = EnableState.Disabled });

        Build(new StubToggleProvider("registry.run", ToggleCapability.Native), scanner, new FakeJournal(), gate: gate)
            .SetEnabled(item, false, Ctx());

        Assert.Equal(1, gate.Calls);
    }

    [Fact]
    public void DecliningTheRestorePointCancelsTheWrite()
    {
        var item = Item();
        var journal = new FakeJournal();

        var result = Build(new StubToggleProvider("registry.run", ToggleCapability.Native),
                           new StubScanProvider("registry.run"), journal,
                           gate: new StubRestorePointGate(allow: false, error: "protection off"))
            .SetEnabled(item, false, Ctx());

        Assert.True(result.Cancelled);
        Assert.Equal("protection off", result.Error);
        Assert.Empty(journal.All);
    }

    [Fact]
    public void AFailedVerificationRollsBackAndJournalsNothing()
    {
        var item = Item();
        var toggle = new StubToggleProvider("registry.run", ToggleCapability.Native);
        // The read-back still reports Enabled, so the disable did not land.
        var scanner = new StubScanProvider("registry.run", item with { State = EnableState.Enabled });
        var journal = new FakeJournal();

        var result = Build(toggle, scanner, journal).SetEnabled(item, enabled: false, Ctx());

        Assert.False(result.Success);
        Assert.Contains("rolled back", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Single(toggle.Restored);
        Assert.Empty(journal.All);
    }

    [Fact]
    public void AThrowingWriteIsReportedAndJournalsNothing()
    {
        var item = Item();
        var toggle = new StubToggleProvider("registry.run", ToggleCapability.Native)
        {
            ThrowOnWrite = new UnauthorizedAccessException("denied"),
        };
        var journal = new FakeJournal();

        var result = Build(toggle, new StubScanProvider("registry.run"), journal).SetEnabled(item, false, Ctx());

        Assert.False(result.Success);
        Assert.Contains("denied", result.Error!);
        Assert.Empty(journal.All);
    }

    [Fact]
    public void BackupRemoveDisableVerifiesTheValueIsGone()
    {
        var item = Item(category: StartupCategory.Winlogon, name: "Taskman");
        var toggle = new StubToggleProvider("registry.run", ToggleCapability.BackupRemove)
        {
            Mechanism = "BackupRemove", After = null,
        };
        var journal = new FakeJournal();

        var result = Build(toggle, new StubScanProvider("registry.run"), journal).SetEnabled(item, false, Ctx());

        Assert.True(result.Success);
        Assert.Single(journal.All);
    }

    [Fact]
    public void BackupRemoveDisableFailsWhenTheValueIsStillThere()
    {
        var item = Item(category: StartupCategory.Winlogon, name: "Taskman");
        var toggle = new StubToggleProvider("registry.run", ToggleCapability.BackupRemove)
        {
            Mechanism = "BackupRemove", After = null,
        };
        var scanner = new StubScanProvider("registry.run", item);
        var journal = new FakeJournal();

        var result = Build(toggle, scanner, journal).SetEnabled(item, false, Ctx());

        Assert.False(result.Success);
        Assert.Single(toggle.Restored);
        Assert.Empty(journal.All);
    }

    [Fact]
    public void BackupRemoveReplacementVerifiesTheReplacementValue()
    {
        var item = Item(category: StartupCategory.Winlogon, name: "Userinit",
                        raw: @"C:\Windows\system32\userinit.exe,C:\evil.exe");
        var toggle = new StubToggleProvider("registry.run", ToggleCapability.BackupRemove)
        {
            Mechanism = "BackupRemove", After = @"expand:%systemroot%\system32\userinit.exe,",
        };
        var scanner = new StubScanProvider("registry.run",
            item with { RawValue = @"%systemroot%\system32\userinit.exe," });

        var result = Build(toggle, scanner, new FakeJournal()).SetEnabled(item, false, Ctx());

        Assert.True(result.Success);
    }

    [Fact]
    public void AProviderWithNoToggleImplementationIsReportedNotCrashed()
    {
        var item = Item();

        var result = Build(new StubToggleProvider("some.other.provider", ToggleCapability.Native),
                           new StubScanProvider("registry.run"), new FakeJournal())
            .SetEnabled(item, false, Ctx());

        Assert.False(result.Success);
        Assert.Contains("registry.run", result.Error!);
    }

    [Fact]
    public void TheCommittedRecordCarriesTheSessionIdAndCurrentTimestamp()
    {
        var item = Item();
        var scanner = new StubScanProvider("registry.run", item with { State = EnableState.Disabled });
        var clock = new FakeClock(new DateTime(2026, 7, 30, 10, 30, 0, DateTimeKind.Utc));
        var journal = new FakeJournal();
        var service = new ToggleService(
            new IToggleProvider[] { new StubToggleProvider("registry.run", ToggleCapability.Native) },
            new IStartupProvider[] { scanner }, journal, new StubRestorePointGate(),
            new RecordingConfirmationSink(), clock, Session);

        service.SetEnabled(item, false, Ctx());

        var record = journal.All.Single();
        Assert.Equal(Session, record.SessionId);
        Assert.Equal(clock.UtcNow, record.Ts);
    }
}
