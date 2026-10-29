using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;

namespace StartupScanner.Core.Writing;

/// <summary>
/// Winlogon's BackupRemove toggle. Userinit and Shell are restored to the OS default rather than
/// deleted: a machine with no Userinit value cannot complete logon, so deletion would violate the
/// "never make the machine unbootable" rule even though the prior value is journaled.
/// </summary>
public static class WinlogonToggle
{
    public const string DefaultUserinit = @"%systemroot%\system32\userinit.exe,";
    public const string DefaultShell = "explorer.exe";

    public static RegistryValueToggle Create(IRegistry registry, IClock clock)
        => new(WinlogonProvider.ProviderIdValue, registry, clock, SafeDefaultFor);

    private static (object Value, RegistryValueType Type)? SafeDefaultFor(StartupItem item)
    {
        if (item.Name.Equals("Userinit", StringComparison.OrdinalIgnoreCase))
            return (DefaultUserinit, RegistryValueType.ExpandString);

        if (item.Name.Equals("Shell", StringComparison.OrdinalIgnoreCase))
            return (DefaultShell, RegistryValueType.String);

        return null;
    }
}
