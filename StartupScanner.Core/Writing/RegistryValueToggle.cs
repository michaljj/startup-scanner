using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>
/// The BackupRemove mechanism, used wherever Windows has no disable flag: journal the current
/// value, then remove it — or replace it with a supplied safe default where removal would be
/// dangerous. Re-enable rewrites the journalled value.
/// </summary>
public sealed class RegistryValueToggle(
    string providerId,
    IRegistry registry,
    IClock clock,
    Func<StartupItem, (object Value, RegistryValueType Type)?>? replacement = null) : IToggleProvider
{
    public string ProviderId => providerId;

    public ToggleCapability GetCapability(StartupItem item)
        => Guardrails.Evaluate(item).Blocked ? ToggleCapability.Blocked : ToggleCapability.BackupRemove;

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
        => enabled ? Enable(item, journal) : Disable(item);

    public void Restore(WriteRecord record) => RegistryWriteOps.Restore(registry, record);

    private WriteRecord Disable(StartupItem item)
    {
        var view = RegistryWriteOps.ViewOf(item.Extra.GetValueOrDefault("View"));

        using var key = registry.OpenKey(item.Location, view, writable: true)
            ?? throw new InvalidOperationException($"Key not found or not writable: {item.Location}");

        var before = RegistryValueCodec.Encode(key.GetValue(item.Name), key.GetValueType(item.Name));
        var swap = replacement?.Invoke(item);

        if (swap is { } value) key.SetValue(item.Name, value.Value, value.Type);
        else key.DeleteValue(item.Name);

        return Build(item, view, enabled: false, before,
                     swap is { } s ? RegistryValueCodec.Encode(s.Value, s.Type) : null);
    }

    private WriteRecord Enable(StartupItem item, IWriteJournalReader journal)
    {
        var backup = journal.All.LastOrDefault(r => r.ItemId == item.Id && !r.Enabled && r.Before is not null)
            ?? throw new InvalidOperationException(
                $"No backed-up value is recorded for '{item.Name}', so it cannot be re-enabled.");

        var view = RegistryWriteOps.ViewOf(item.Extra.GetValueOrDefault("View"));
        var (value, type) = RegistryValueCodec.Decode(backup.Before!);

        using var key = registry.CreateKey(item.Location, view);
        key.SetValue(item.Name, value, type);

        return Build(item, view, enabled: true, before: null, after: backup.Before);
    }

    private WriteRecord Build(StartupItem item, RegistryViewKind view, bool enabled, string? before, string? after)
        => new()
        {
            Ts = clock.UtcNow,
            SessionId = "",
            ItemId = item.Id,
            ProviderId = providerId,
            Mechanism = "BackupRemove",
            Location = item.Location,
            Name = item.Name,
            Category = item.Category,
            UserSid = item.UserSid,
            View = view == RegistryViewKind.Default ? null : view.ToString(),
            Before = before,
            After = after,
            Enabled = enabled,
        };
}
