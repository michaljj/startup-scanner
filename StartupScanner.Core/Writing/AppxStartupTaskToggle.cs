using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

public sealed class AppxStartupTaskToggle(IRegistry registry, IClock clock) : IToggleProvider
{
    public string ProviderId => "appx-startup";

    public ToggleCapability GetCapability(StartupItem item)
    {
        if (Guardrails.Evaluate(item).Blocked) return ToggleCapability.Blocked;

        // Refusing to write an unrecognised layout is the point: guessing here could corrupt the
        // package's own state.
        return item.Extra.GetValueOrDefault("StateRecognised") == "True"
            ? ToggleCapability.Native
            : ToggleCapability.Blocked;
    }

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
    {
        using var key = registry.OpenKey(item.Location, RegistryViewKind.Default, writable: true)
            ?? throw new InvalidOperationException($"AppX startup task key not found: {item.Location}");

        var before = RegistryValueCodec.Encode(key.GetValue("State"), RegistryValueType.Dword);
        var target = enabled ? AppxStartupTaskProvider.EnabledState : AppxStartupTaskProvider.DisabledState;
        key.SetValue("State", target, RegistryValueType.Dword);

        return new WriteRecord
        {
            Ts = clock.UtcNow,
            SessionId = "",
            ItemId = item.Id,
            ProviderId = ProviderId,
            Mechanism = "Appx.State",
            Location = item.Location,
            Name = "State",
            Category = item.Category,
            UserSid = item.UserSid,
            Before = before,
            After = RegistryValueCodec.Encode(target, RegistryValueType.Dword),
            Enabled = enabled,
        };
    }

    public void Restore(WriteRecord record) => RegistryWriteOps.Restore(registry, record);
}
