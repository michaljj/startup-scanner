using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>
/// The Native disable mechanism: flip the StartupApproved blob that Task Manager and msconfig
/// read, so this app's changes stay consistent with theirs.
/// </summary>
public static class StartupApprovedToggle
{
    public static WriteRecord Write(IRegistry registry, IClock clock, string providerId, StartupItem item,
                                    string approvedKeyPath, RegistryViewKind view, bool enabled)
    {
        using var key = registry.CreateKey(approvedKeyPath, view);

        var existing = key.GetValue(item.Name) as byte[];
        var before = existing is null ? null : RegistryValueCodec.Encode(existing, RegistryValueType.Binary);
        var blob = StartupApprovedBlob.Encode(enabled, clock.UtcNow, existing);

        key.SetValue(item.Name, blob, RegistryValueType.Binary);

        return new WriteRecord
        {
            Ts = clock.UtcNow,
            SessionId = "",
            ItemId = item.Id,
            ProviderId = providerId,
            Mechanism = "StartupApproved",
            Location = approvedKeyPath,
            Name = item.Name,
            Category = item.Category,
            UserSid = item.UserSid,
            View = view == RegistryViewKind.Default ? null : view.ToString(),
            Before = before,
            After = RegistryValueCodec.Encode(blob, RegistryValueType.Binary),
            Enabled = enabled,
        };
    }
}
