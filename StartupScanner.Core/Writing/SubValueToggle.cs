using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>
/// BackupRemove for providers whose item Name is a composite (IFEO's "sethc.exe:Debugger").
/// The registry value written is Extra["ValueName"], falling back to Name when absent.
/// </summary>
public sealed class SubValueToggle(string providerId, IRegistry registry, IClock clock) : IToggleProvider
{
    private readonly RegistryValueToggle _inner = new(providerId, registry, clock);

    public string ProviderId => providerId;

    public ToggleCapability GetCapability(StartupItem item)
        => Guardrails.Evaluate(item).Blocked ? ToggleCapability.Blocked : ToggleCapability.BackupRemove;

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
        => _inner.SetEnabled(Retarget(item), enabled, journal) with { ItemId = item.Id };

    public void Restore(WriteRecord record) => _inner.Restore(record);

    /// <summary>
    /// Points Name at the real registry value while keeping the item's identity, so the journal
    /// entry still resolves back to the row the user clicked.
    /// </summary>
    private static StartupItem Retarget(StartupItem item)
        => item.Extra.TryGetValue("ValueName", out var valueName) && valueName.Length > 0
            ? item with { Name = valueName }
            : item;
}
