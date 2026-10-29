using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

public sealed class ServiceToggle(IRegistry registry, IClock clock) : IToggleProvider
{
    public const int DisabledStart = 4;

    /// <summary>
    /// Used only when nothing was journalled, i.e. the service was already disabled before this app
    /// first ran. Manual is the conservative choice: an unexpected Automatic can start something at
    /// boot that the user never asked for.
    /// </summary>
    public const int FallbackEnabledStart = 3;

    public string ProviderId => "service";

    public ToggleCapability GetCapability(StartupItem item)
        => Guardrails.Evaluate(item).Blocked ? ToggleCapability.Blocked : ToggleCapability.Native;

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
    {
        using var key = registry.OpenKey(item.Location, RegistryViewKind.Default, writable: true)
            ?? throw new InvalidOperationException($"Service key not found or not writable: {item.Location}");

        var current = key.GetValue("Start");
        var before = current is null ? null : RegistryValueCodec.Encode(Convert.ToInt32(current), RegistryValueType.Dword);
        var target = enabled ? PriorStart(item, journal) : DisabledStart;

        key.SetValue("Start", target, RegistryValueType.Dword);

        return new WriteRecord
        {
            Ts = clock.UtcNow,
            SessionId = "",
            ItemId = item.Id,
            ProviderId = ProviderId,
            Mechanism = "Service.Start",
            Location = item.Location,
            Name = "Start",
            Category = item.Category,
            UserSid = null,
            Before = before,
            After = RegistryValueCodec.Encode(target, RegistryValueType.Dword),
            Enabled = enabled,
        };
    }

    public void Restore(WriteRecord record) => RegistryWriteOps.Restore(registry, record);

    private static int PriorStart(StartupItem item, IWriteJournalReader journal)
    {
        var backup = journal.All.LastOrDefault(r => r.ItemId == item.Id && !r.Enabled && r.Before is not null);
        if (backup is null) return FallbackEnabledStart;

        var (value, _) = RegistryValueCodec.Decode(backup.Before!);
        return Convert.ToInt32(value);
    }
}
