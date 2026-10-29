using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Scanning;
using StartupScanner.Core.Storage;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Scanning;

public class ScanEngineTests
{
    private sealed class StubProvider(string id, StartupCategory category, params StartupItem[] items) : IStartupProvider
    {
        public string Id => id;
        public StartupCategory Category => category;
        public IEnumerable<StartupItem> Scan(ScanContext ctx) => items;
    }

    private sealed class ThrowingProvider(string id) : IStartupProvider
    {
        public string Id => id;
        public StartupCategory Category => StartupCategory.RunKey;
        public IEnumerable<StartupItem> Scan(ScanContext ctx) => throw new InvalidOperationException("provider exploded");
    }

    private static StartupItem Item(string provider, string location, string name,
                                    StartupCategory category = StartupCategory.RunKey,
                                    IDictionary<string, string>? extra = null) => new()
    {
        Id = Identity.Compute(provider, null, location, name),
        ProviderId = provider, Location = location, Name = name, RawValue = "cmd", Category = category,
        Extra = new Dictionary<string, string>(extra ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
    };

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    [Fact]
    public void CollectsItemsFromEveryProvider()
    {
        var engine = new ScanEngine(new IStartupProvider[]
        {
            new StubProvider("a", StartupCategory.RunKey, Item("a", "l1", "n1")),
            new StubProvider("b", StartupCategory.Service, Item("b", "l2", "n2")),
        }, new FakeJournal());

        var result = engine.Run(Ctx());

        Assert.Equal(2, result.Items.Count);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void AThrowingProviderBecomesAScanErrorAndTheOthersStillComplete()
    {
        var engine = new ScanEngine(new IStartupProvider[]
        {
            new ThrowingProvider("bad"),
            new StubProvider("good", StartupCategory.RunKey, Item("good", "l", "n")),
        }, new FakeJournal());

        var result = engine.Run(Ctx());

        Assert.Single(result.Items);
        var error = Assert.Single(result.Errors);
        Assert.Equal("bad", error.ProviderId);
        Assert.Contains("provider exploded", error.Message);
    }

    [Fact]
    public void PerEntryErrorsFromTheContextAreIncludedInTheResult()
    {
        var ctx = Ctx();
        ctx.Errors.Add(new ScanError("a", "some\\key", "access denied"));
        var engine = new ScanEngine(new IStartupProvider[] { new StubProvider("a", StartupCategory.RunKey) }, new FakeJournal());

        Assert.Single(engine.Run(ctx).Errors);
    }

    [Fact]
    public void StampsRiskTierOnEveryItem()
    {
        var engine = new ScanEngine(new IStartupProvider[]
        {
            new StubProvider("a", StartupCategory.RunKey, Item("a", "l", "Spotify")),
            new StubProvider("b", StartupCategory.Service,
                Item("b", "l", "RpcSs", StartupCategory.Service, new Dictionary<string, string> { ["Start"] = "2" })),
            new StubProvider("c", StartupCategory.Winlogon, Item("c", "l", "Taskman", StartupCategory.Winlogon)),
        }, new FakeJournal());

        var items = engine.Run(Ctx()).Items;

        Assert.Equal(RiskTier.Low, items.Single(i => i.Name == "Spotify").Risk);
        Assert.Equal(RiskTier.Critical, items.Single(i => i.Name == "RpcSs").Risk);
        Assert.Equal(RiskTier.High, items.Single(i => i.Name == "Taskman").Risk);
    }

    [Fact]
    public void SortsDeterministicallyByProviderLocationName()
    {
        var engine = new ScanEngine(new IStartupProvider[]
        {
            new StubProvider("z", StartupCategory.RunKey, Item("z", "l", "b"), Item("z", "l", "a")),
            new StubProvider("a", StartupCategory.RunKey, Item("a", "l2", "x"), Item("a", "l1", "y")),
        }, new FakeJournal());

        var keys = engine.Run(Ctx()).Items.Select(i => $"{i.ProviderId}|{i.Location}|{i.Name}").ToArray();

        Assert.Equal(new[] { "a|l1|y", "a|l2|x", "z|l|a", "z|l|b" }, keys);
    }

    [Fact]
    public void ReInjectsABackupRemoveDisabledEntryThatNoProviderCanStillSee()
    {
        var journal = new FakeJournal();
        var removedId = Identity.Compute("winlogon", null, @"HKLM\SOFTWARE\Winlogon", "Taskman");
        journal.Add(new WriteRecord
        {
            Ts = new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc),
            SessionId = "s1", ItemId = removedId, ProviderId = "winlogon", Mechanism = "BackupRemove",
            Location = @"HKLM\SOFTWARE\Winlogon", Name = "Taskman", Category = StartupCategory.Winlogon,
            Before = @"sz:C:\taskman.exe", After = null, Enabled = false,
        });

        var engine = new ScanEngine(new IStartupProvider[] { new StubProvider("winlogon", StartupCategory.Winlogon) }, journal);

        var item = Assert.Single(engine.Run(Ctx()).Items);

        Assert.Equal(removedId, item.Id);
        Assert.Equal("Taskman", item.Name);
        Assert.Equal(@"C:\taskman.exe", item.RawValue);
        Assert.Equal(EnableState.Disabled, item.State);
        Assert.Equal("RemovedByScanner", item.StateSource);
        Assert.Equal(StartupCategory.Winlogon, item.Category);
    }

    [Fact]
    public void DoesNotReInjectWhenTheEntryIsPresentAgain()
    {
        var journal = new FakeJournal();
        var id = Identity.Compute("winlogon", null, @"HKLM\SOFTWARE\Winlogon", "Taskman");
        journal.Add(new WriteRecord
        {
            Ts = new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc),
            SessionId = "s1", ItemId = id, ProviderId = "winlogon", Mechanism = "BackupRemove",
            Location = @"HKLM\SOFTWARE\Winlogon", Name = "Taskman", Category = StartupCategory.Winlogon,
            Before = @"sz:C:\taskman.exe", After = null, Enabled = false,
        });

        var live = Item("winlogon", @"HKLM\SOFTWARE\Winlogon", "Taskman", StartupCategory.Winlogon);
        var engine = new ScanEngine(new IStartupProvider[] { new StubProvider("winlogon", StartupCategory.Winlogon, live) }, journal);

        var item = Assert.Single(engine.Run(Ctx()).Items);

        Assert.NotEqual("RemovedByScanner", item.StateSource);
    }

    [Fact]
    public void DoesNotReInjectAfterTheEntryWasReEnabled()
    {
        var journal = new FakeJournal();
        var id = Identity.Compute("winlogon", null, @"HKLM\SOFTWARE\Winlogon", "Taskman");
        journal.Add(new WriteRecord
        {
            Ts = new DateTime(2026, 7, 30, 10, 0, 0, DateTimeKind.Utc),
            SessionId = "s2", ItemId = id, ProviderId = "winlogon", Mechanism = "BackupRemove",
            Location = @"HKLM\SOFTWARE\Winlogon", Name = "Taskman", Category = StartupCategory.Winlogon,
            Before = null, After = @"sz:C:\taskman.exe", Enabled = true,
        });

        var engine = new ScanEngine(new IStartupProvider[] { new StubProvider("winlogon", StartupCategory.Winlogon) }, journal);

        Assert.Empty(engine.Run(Ctx()).Items);
    }

    [Fact]
    public void NativeMechanismRecordsAreNotReInjected()
    {
        var journal = new FakeJournal();
        journal.Add(new WriteRecord
        {
            Ts = new DateTime(2026, 7, 30, 10, 0, 0, DateTimeKind.Utc),
            SessionId = "s1", ItemId = "gone-id", ProviderId = "registry.run", Mechanism = "StartupApproved",
            Location = @"HKCU\Run", Name = "Spotify", Category = StartupCategory.RunKey,
            Before = "binary:AgAAAAAAAAAAAAAA", After = "binary:AwAAAAAAAAAAAAAA", Enabled = false,
        });

        var engine = new ScanEngine(new IStartupProvider[] { new StubProvider("registry.run", StartupCategory.RunKey) }, journal);

        Assert.Empty(engine.Run(Ctx()).Items);
    }

    [Fact]
    public void NonRegistryBackupsAreReInjectedWithTheirStoredTextAsIs()
    {
        var journal = new FakeJournal();
        journal.Add(new WriteRecord
        {
            Ts = new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc),
            SessionId = "s1", ItemId = "wmi-id", ProviderId = "wmi", Mechanism = "Wmi.Instance",
            Location = @"root\subscription:__EventFilter", Name = "EvilFilter",
            Category = StartupCategory.WmiSubscription,
            Before = """{"Name":"EvilFilter","Query":"SELECT * FROM __InstanceCreationEvent"}""",
            After = null, Enabled = false,
        });

        var engine = new ScanEngine(new IStartupProvider[] { new StubProvider("wmi", StartupCategory.WmiSubscription) }, journal);

        var item = Assert.Single(engine.Run(Ctx()).Items);

        Assert.Equal("RemovedByScanner", item.StateSource);
        Assert.Contains("EvilFilter", item.RawValue);
    }

    [Fact]
    public void ManyProvidersRunConcurrentlyWithoutLosingItems()
    {
        var providers = Enumerable.Range(0, 40)
            .Select(i => (IStartupProvider)new StubProvider($"p{i:00}", StartupCategory.RunKey, Item($"p{i:00}", "l", "n")))
            .ToList();

        Assert.Equal(40, new ScanEngine(providers, new FakeJournal()).Run(Ctx()).Items.Count);
    }
}
