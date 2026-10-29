using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Presentation;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>
/// The only path through which anything is written. Order is fixed: guardrail, risk confirmation,
/// restore point, write, read back, journal. A failed read-back rolls the write back and appends
/// nothing, so the journal never records a write that did not land.
/// </summary>
public sealed class ToggleService
{
    private readonly Dictionary<string, IToggleProvider> _toggles;
    private readonly WriteVerifier _verifier;
    private readonly IWriteJournal _journal;
    private readonly IRestorePointGate _restorePoint;
    private readonly IConfirmationSink _confirmations;
    private readonly IClock _clock;
    private readonly string _sessionId;

    public ToggleService(
        IReadOnlyList<IToggleProvider> toggles,
        IReadOnlyList<IStartupProvider> scanners,
        IWriteJournal journal,
        IRestorePointGate restorePoint,
        IConfirmationSink confirmations,
        IClock clock,
        string sessionId)
    {
        _toggles = toggles.ToDictionary(t => t.ProviderId, StringComparer.Ordinal);
        _verifier = new WriteVerifier(scanners);
        _journal = journal;
        _restorePoint = restorePoint;
        _confirmations = confirmations;
        _clock = clock;
        _sessionId = sessionId;
    }

    public ToggleResult SetEnabled(StartupItem item, bool enabled, ScanContext ctx)
    {
        var blocked = Guardrails.Evaluate(item);
        if (blocked.Blocked) return ToggleResult.Fail(blocked.Reason!);

        if (!_toggles.TryGetValue(item.ProviderId, out var toggle))
            return ToggleResult.Fail($"Provider '{item.ProviderId}' has no writable implementation.");

        if (toggle.GetCapability(item) == ToggleCapability.Blocked)
            return ToggleResult.Fail("This entry is shown for reference only and is not writable.");

        if (item.Risk != RiskTier.Low && !_confirmations.Confirm(item, item.Risk, ConfirmationText.For(item, enabled)))
            return ToggleResult.Cancel();

        if (!_restorePoint.Ensure(out var restoreError)) return ToggleResult.Cancel(restoreError);

        WriteRecord record;
        try
        {
            record = toggle.SetEnabled(item, enabled, _journal);
        }
        catch (Exception ex)
        {
            return ToggleResult.Fail(ex.Message);
        }

        var problem = _verifier.Check(record, enabled, ctx);
        if (problem is not null) return RollBack(toggle, record, problem);

        var committed = record with { SessionId = _sessionId, Ts = _clock.UtcNow };
        _journal.Record(committed);
        return ToggleResult.Ok(committed);
    }

    private static ToggleResult RollBack(IToggleProvider toggle, WriteRecord record, string problem)
    {
        try
        {
            toggle.Restore(record);
        }
        catch (Exception ex)
        {
            return ToggleResult.Fail($"{problem} The rollback also failed: {ex.Message}");
        }

        return ToggleResult.Fail($"{problem} The change was rolled back and nothing was journaled.");
    }
}
