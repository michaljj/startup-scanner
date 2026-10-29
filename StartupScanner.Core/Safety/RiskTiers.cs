using StartupScanner.Core.Models;

namespace StartupScanner.Core.Safety;

/// <summary>
/// Risk tier controls the confirmation dialog only. Critical is the tier given to whatever the
/// blocked list already blocks, so those items are never writable and never prompt.
/// </summary>
public static class RiskTiers
{
    public static RiskTier For(StartupItem item)
    {
        if (Guardrails.Evaluate(item).Blocked) return RiskTier.Critical;

        return item.Category switch
        {
            StartupCategory.RunKey or StartupCategory.StartupFolder or StartupCategory.AppxStartupTask
                => RiskTier.Low,

            StartupCategory.ScheduledTask
                => item.Location.StartsWith(@"\Microsoft\Windows", StringComparison.OrdinalIgnoreCase)
                    ? RiskTier.Medium : RiskTier.Low,

            StartupCategory.Winlogon or StartupCategory.BootExecute or StartupCategory.WinsockLsp
                => RiskTier.High,

            // IfeoProvider's Name is composite ("sethc.exe:Debugger"), so the value name comes
            // from Extra; items built without it still compare on Name.
            StartupCategory.Ifeo
                => ValueName(item).Equals("Debugger", StringComparison.OrdinalIgnoreCase) ? RiskTier.High : RiskTier.Medium,

            StartupCategory.AppInit
                => item.Name.StartsWith("AppInit", StringComparison.OrdinalIgnoreCase) ? RiskTier.High : RiskTier.Medium,

            _ => RiskTier.Medium,
        };
    }

    private static string ValueName(StartupItem item)
        => item.Extra.TryGetValue("ValueName", out var name) && name.Length > 0 ? name : item.Name;
}
