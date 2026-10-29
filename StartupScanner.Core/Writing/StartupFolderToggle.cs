using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>
/// Startup folder shortcuts disable through StartupApproved\StartupFolder, keyed by file name.
/// The shortcut file itself is never moved or deleted.
/// </summary>
public sealed class StartupFolderToggle(IRegistry registry, IClock clock) : IToggleProvider
{
    public string ProviderId => "startup.folder";

    public ToggleCapability GetCapability(StartupItem item)
        => Guardrails.Evaluate(item).Blocked ? ToggleCapability.Blocked : ToggleCapability.Native;

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
        => StartupApprovedToggle.Write(registry, clock, ProviderId, item,
            StartupApprovedReader.ApprovedKeyPath(item.Extra["ApprovedRoot"], "StartupFolder"),
            RegistryViewKind.Default, enabled);

    public void Restore(WriteRecord record) => RegistryWriteOps.Restore(registry, record);
}
