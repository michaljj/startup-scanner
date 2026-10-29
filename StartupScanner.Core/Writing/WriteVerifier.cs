using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>
/// Reads a write back through the owning scan provider and reports any mismatch. Returning null
/// means the write landed exactly as intended, which is the only case in which the caller may
/// append a journal entry.
/// </summary>
public sealed class WriteVerifier(IReadOnlyList<IStartupProvider> scanners)
{
    private readonly Dictionary<string, IStartupProvider> _scanners =
        scanners.ToDictionary(s => s.Id, StringComparer.Ordinal);

    public string? Check(WriteRecord record, bool expectedEnabled, ScanContext ctx)
    {
        if (!_scanners.TryGetValue(record.ProviderId, out var scanner))
            return $"No scan provider registered for '{record.ProviderId}', so the write cannot be verified.";

        StartupItem? found;
        try
        {
            found = scanner.Scan(ctx).FirstOrDefault(i => i.Id == record.ItemId);
        }
        catch (Exception ex)
        {
            return $"The change could not be read back: {ex.Message}";
        }

        if (record.Mechanism == "BackupRemove") return CheckBackupRemove(record, found);

        if (found is null) return "The entry could not be found after the write.";

        var expected = expectedEnabled ? EnableState.Enabled : EnableState.Disabled;
        return found.State == expected ? null : $"Read back {found.State} but expected {expected}.";
    }

    private static string? CheckBackupRemove(WriteRecord record, StartupItem? found)
    {
        if (record.After is null)
            return found is null ? null : "The value was still present after it was removed.";

        if (found is null) return "The value was not present after it was written.";

        var (value, type) = RegistryValueCodec.Decode(record.After);
        var expected = RegistryValueCodec.ToDisplay(value, type);
        return found.RawValue == expected ? null : $"Read back '{found.RawValue}' but expected '{expected}'.";
    }
}
