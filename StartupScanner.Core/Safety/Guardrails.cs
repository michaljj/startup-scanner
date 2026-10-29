using StartupScanner.Core.Models;

namespace StartupScanner.Core.Safety;

public sealed record BlockDecision(bool Blocked, string? Reason)
{
    public static readonly BlockDecision Allowed = new(false, null);

    public static BlockDecision Block(string reason) => new(true, reason);
}

/// <summary>
/// The hard "never make the machine unbootable" rules. Items these block are shown and fully
/// detailed but are never writable, and RiskTiers reports them as Critical so no confirmation
/// dialog is ever reached for them.
/// </summary>
public static class Guardrails
{
    public static readonly IReadOnlyList<string> CriticalServices = new[]
    {
        "DcomLaunch", "RpcSs", "RpcEptMapper", "PlugPlay", "Power",
        "ProfSvc", "LSM", "BrokerInfrastructure", "SamSs", "EventLog",
    };

    public static BlockDecision Evaluate(StartupItem item) => item.Category switch
    {
        StartupCategory.Winlogon => EvaluateWinlogon(item),
        StartupCategory.BootExecute => EvaluateBootExecute(item),
        StartupCategory.Service or StartupCategory.Driver => EvaluateService(item),
        StartupCategory.Lsa => BlockDecision.Block(
            "LSA package entries are blocked as a category: removing an authentication package can prevent logon, "
            + "and legitimate additions are rare enough that manual editing is the safer path."),
        _ => BlockDecision.Allowed,
    };

    private static BlockDecision EvaluateWinlogon(StartupItem item)
    {
        if (Is(item.Name, "Userinit") && IsDefault(item.RawValue, @"system32\userinit.exe"))
            return BlockDecision.Block(
                "Userinit is the default logon process. Removing it prevents logon from completing.");

        if (Is(item.Name, "Shell") && IsDefault(item.RawValue, "explorer.exe"))
            return BlockDecision.Block(
                "Shell is the default Windows shell. Removing it leaves the desktop with no shell after logon.");

        return BlockDecision.Allowed;
    }

    private static BlockDecision EvaluateBootExecute(StartupItem item)
        => Is(item.Name, "BootExecute") && item.RawValue.Trim() == "autocheck autochk *"
            ? BlockDecision.Block("'autocheck autochk *' is the boot-time disk check. Removing it risks an unbootable system.")
            : BlockDecision.Allowed;

    private static BlockDecision EvaluateService(StartupItem item)
    {
        if (CriticalServices.Contains(item.Name, StringComparer.OrdinalIgnoreCase))
            return BlockDecision.Block($"'{item.Name}' is on the named critical-service list. Disabling it prevents Windows from starting correctly.");

        if (item.Extra.TryGetValue("Start", out var start) && int.TryParse(start, out var value) && value is 0 or 1)
            return BlockDecision.Block($"Start={value} means this loads at boot or system start. Disabling it risks an unbootable system.");

        return BlockDecision.Allowed;
    }

    private static bool Is(string name, string expected) => name.Equals(expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the value is exactly the OS default, ignoring case, the %SystemRoot% prefix, and
    /// the trailing comma Windows writes on Userinit. An appended second command makes it false.
    /// </summary>
    private static bool IsDefault(string rawValue, string defaultTail)
    {
        var value = rawValue.Trim().TrimEnd(',').Trim();
        if (value.Contains(',')) return false;
        return value.Equals(defaultTail, StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(@"\" + defaultTail, StringComparison.OrdinalIgnoreCase);
    }
}
