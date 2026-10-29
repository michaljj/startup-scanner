namespace StartupScanner.Core.Models;

public enum StartupCategory
{
    RunKey, StartupFolder, Service, Driver, ScheduledTask, Winlogon, BootExecute,
    ActiveSetup, Ifeo, AppInit, ExplorerHook, AppxStartupTask, WmiSubscription,
    ComHijack, Lsa, NetshHelper, PrintMonitor, WinsockLsp, GpoScript
}

public enum EnableState { Enabled, Disabled, Unknown }

public enum RiskTier { Low, Medium, High, Critical }

public enum ToggleCapability { Native, BackupRemove, Blocked }

public sealed record StartupItem
{
    public required string Id { get; init; }
    public required string ProviderId { get; init; }
    public required string Location { get; init; }
    public required string Name { get; init; }
    public required string RawValue { get; init; }
    public required StartupCategory Category { get; init; }
    public string? ImagePath { get; init; }
    public string? Arguments { get; init; }
    public EnableState State { get; init; } = EnableState.Unknown;
    public string StateSource { get; init; } = "";
    public RiskTier Risk { get; init; } = RiskTier.Low;
    public string? UserSid { get; init; }
    public FileFacts? File { get; init; }

    /// <summary>Set when the entry was found but some of its data could not be read.</summary>
    public bool PartiallyRead { get; init; }

    /// <summary>Provider-specific facts for the details panel. Serialised into the snapshot.</summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
