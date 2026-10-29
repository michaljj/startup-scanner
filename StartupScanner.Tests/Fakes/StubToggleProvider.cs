using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;
using StartupScanner.Core.Writing;

namespace StartupScanner.Tests.Fakes;

/// <summary>Toggle whose write is a no-op, so ToggleService's own behaviour is what gets tested.</summary>
public sealed class StubToggleProvider(string providerId, ToggleCapability capability) : IToggleProvider
{
    public string ProviderId => providerId;
    public List<WriteRecord> Restored { get; } = new();
    public Exception? ThrowOnWrite { get; set; }
    public string Mechanism { get; set; } = "StartupApproved";
    public string? After { get; set; } = "binary:AwAAAAAAAAAAAAAA";

    public ToggleCapability GetCapability(StartupItem item) => capability;

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
    {
        if (ThrowOnWrite is not null) throw ThrowOnWrite;

        return new WriteRecord
        {
            Ts = new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc),
            SessionId = "",
            ItemId = item.Id,
            ProviderId = providerId,
            Mechanism = Mechanism,
            Location = item.Location,
            Name = item.Name,
            Category = item.Category,
            Before = "binary:AgAAAAAAAAAAAAAA",
            After = After,
            Enabled = enabled,
        };
    }

    public void Restore(WriteRecord record) => Restored.Add(record);
}

/// <summary>Scan provider whose result the test controls, standing in for the read-back.</summary>
public sealed class StubScanProvider(string id, params StartupItem[] items) : Core.Providers.IStartupProvider
{
    public string Id => id;
    public StartupCategory Category => StartupCategory.RunKey;
    public List<StartupItem> Items { get; } = items.ToList();
    public IEnumerable<StartupItem> Scan(ScanContext ctx) => Items;
}
