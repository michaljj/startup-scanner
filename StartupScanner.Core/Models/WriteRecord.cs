namespace StartupScanner.Core.Models;

/// <summary>
/// One line of writes.jsonl. Category and UserSid are carried so that ScanEngine can
/// re-inject a BackupRemove-disabled entry that no longer exists in the registry:
/// without them the entry would silently vanish from the list and become un-re-enableable.
/// </summary>
public sealed record WriteRecord
{
    public required DateTime Ts { get; init; }
    public required string SessionId { get; init; }
    public required string ItemId { get; init; }
    public required string ProviderId { get; init; }

    /// <summary>"StartupApproved" | "Service.Start" | "Task.Enabled" | "BackupRemove" | "Appx.State" | "Wmi.Instance" | "GpoScript.Ini"</summary>
    public required string Mechanism { get; init; }

    /// <summary>Key or task path the write targeted — not necessarily the item's own Location.</summary>
    public required string Location { get; init; }

    /// <summary>Value name the write targeted, e.g. "Start" for a service.</summary>
    public required string Name { get; init; }

    public required StartupCategory Category { get; init; }
    public string? UserSid { get; init; }

    /// <summary>
    /// RegistryViewKind name when the write was view-specific. Undo of a WOW6432Node Run key must
    /// go back to the 32-bit view, so the view cannot be re-derived at undo time.
    /// </summary>
    public string? View { get; init; }

    /// <summary>Prior value, RegistryValueCodec-encoded. Null means the value did not exist.</summary>
    public string? Before { get; init; }

    /// <summary>Value written, RegistryValueCodec-encoded. Null means the value was removed.</summary>
    public string? After { get; init; }

    /// <summary>Target state of this write: true = the entry was enabled, false = disabled.</summary>
    public required bool Enabled { get; init; }
}
