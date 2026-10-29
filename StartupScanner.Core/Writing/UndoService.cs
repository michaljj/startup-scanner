using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>
/// Reverse-application of writes.jsonl. Each undo puts the record's Before value back, verifies it
/// the same way a forward write is verified, and appends an inverse record so the journal stays
/// append-only.
/// </summary>
public sealed class UndoService
{
    private const string UndoSuffix = ":undo";

    private readonly Dictionary<string, IToggleProvider> _toggles;
    private readonly WriteVerifier _verifier;
    private readonly IWriteJournal _journal;
    private readonly IClock _clock;
    private readonly string _sessionId;
    private readonly HashSet<WriteRecord> _undone = new(ReferenceEqualityComparer.Instance);

    public UndoService(
        IReadOnlyList<IToggleProvider> toggles,
        IReadOnlyList<IStartupProvider> scanners,
        IWriteJournal journal,
        IClock clock,
        string sessionId)
    {
        _toggles = toggles.ToDictionary(t => t.ProviderId, StringComparer.Ordinal);
        _verifier = new WriteVerifier(scanners);
        _journal = journal;
        _clock = clock;
        _sessionId = sessionId;
    }

    public ToggleResult UndoLast(ScanContext ctx)
    {
        var record = _journal.All.LastOrDefault(r => !IsUndo(r) && !_undone.Contains(r));
        return record is null
            ? ToggleResult.Fail("There is nothing left to undo.")
            : Reverse(record, ctx);
    }

    public IReadOnlyList<ToggleResult> UndoSession(ScanContext ctx)
    {
        var pending = _journal.All
            .Where(r => r.SessionId == _sessionId && !_undone.Contains(r))
            .Reverse()
            .ToList();

        var results = new List<ToggleResult>();
        foreach (var record in pending)
        {
            var result = Reverse(record, ctx);
            results.Add(result);
            if (!result.Success) break;
        }
        return results;
    }

    private ToggleResult Reverse(WriteRecord record, ScanContext ctx)
    {
        if (!_toggles.TryGetValue(record.ProviderId, out var toggle))
            return ToggleResult.Fail($"Provider '{record.ProviderId}' has no writable implementation.");

        try
        {
            toggle.Restore(record);
        }
        catch (Exception ex)
        {
            return ToggleResult.Fail($"Undo failed: {ex.Message}");
        }

        var inverse = record with
        {
            Ts = _clock.UtcNow,
            SessionId = _sessionId + UndoSuffix,
            Before = record.After,
            After = record.Before,
            Enabled = !record.Enabled,
        };

        var problem = _verifier.Check(inverse, inverse.Enabled, ctx);
        if (problem is not null) return ToggleResult.Fail($"{problem} The undo was not journaled.");

        _undone.Add(record);
        _journal.Record(inverse);
        return ToggleResult.Ok(inverse);
    }

    private static bool IsUndo(WriteRecord record)
        => record.SessionId.EndsWith(UndoSuffix, StringComparison.Ordinal);
}
