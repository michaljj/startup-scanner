using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

public sealed class RunKeyToggle(IRegistry registry, IClock clock) : IToggleProvider
{
    private readonly RegistryValueToggle _fallback = new("registry.run", registry, clock);

    public string ProviderId => "registry.run";

    public ToggleCapability GetCapability(StartupItem item)
    {
        if (Guardrails.Evaluate(item).Blocked) return ToggleCapability.Blocked;
        return HasApprovedCoverage(item) ? ToggleCapability.Native : ToggleCapability.BackupRemove;
    }

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
    {
        if (!HasApprovedCoverage(item)) return _fallback.SetEnabled(item, enabled, journal);

        return StartupApprovedToggle.Write(registry, clock, ProviderId, item,
            StartupApprovedReader.ApprovedKeyPath(item.Extra["ApprovedRoot"], item.Extra["ApprovedSubKey"]),
            RegistryWriteOps.ViewOf(item.Extra.GetValueOrDefault("View")), enabled);
    }

    public void Restore(WriteRecord record)
    {
        if (record.Mechanism == "BackupRemove") _fallback.Restore(record);
        else RegistryWriteOps.Restore(registry, record);
    }

    /// <summary>RunOnce, RunServices, and the policy Run keys have no StartupApproved state.</summary>
    private static bool HasApprovedCoverage(StartupItem item)
        => !string.IsNullOrEmpty(item.Extra.GetValueOrDefault("ApprovedSubKey"))
        && !string.IsNullOrEmpty(item.Extra.GetValueOrDefault("ApprovedRoot"));
}
