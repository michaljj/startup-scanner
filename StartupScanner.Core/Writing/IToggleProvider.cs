using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

public sealed record ToggleResult(bool Success, bool Cancelled, string? Error, WriteRecord? Record)
{
    public static ToggleResult Ok(WriteRecord record) => new(true, false, null, record);
    public static ToggleResult Fail(string error) => new(false, false, error, null);
    public static ToggleResult Cancel(string? reason = null) => new(false, true, reason, null);
}

public interface IConfirmationSink
{
    /// <summary>Returns true to proceed. Never called for Low-risk items.</summary>
    bool Confirm(StartupItem item, RiskTier tier, string message);
}

public interface IToggleProvider
{
    /// <summary>Matches the scan provider's Id, which is how ToggleService finds this toggle.</summary>
    string ProviderId { get; }

    ToggleCapability GetCapability(StartupItem item);

    /// <summary>
    /// Performs the write and returns an UNCOMMITTED record describing it. Implementations must
    /// not call IWriteJournal.Record: only ToggleService may, and only after reading the value
    /// back. SessionId is left empty for ToggleService to stamp.
    /// </summary>
    WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal);

    /// <summary>Writes record.Before back. Used both to roll back a failed write and to undo.</summary>
    void Restore(WriteRecord record);
}
