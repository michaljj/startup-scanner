using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>
/// Toggle for categories that are shown and fully detailed but never writable. Registering this
/// instead of omitting a toggle makes the refusal structural: there is no code path that writes.
/// </summary>
public sealed class BlockedToggle(string providerId) : IToggleProvider
{
    public string ProviderId => providerId;

    public ToggleCapability GetCapability(StartupItem item) => ToggleCapability.Blocked;

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
        => throw new InvalidOperationException(
            $"'{item.Name}' is in a category this app never writes to. Edit it manually if you are sure.");

    public void Restore(WriteRecord record)
        => throw new InvalidOperationException($"'{record.Name}' is in a category this app never writes to.");
}
