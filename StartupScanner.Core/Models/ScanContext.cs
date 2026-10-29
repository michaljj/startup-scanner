using System.Collections.Concurrent;

namespace StartupScanner.Core.Models;

public sealed record UserProfile(string Sid, string ProfilePath, bool HiveLoaded, string? UserName);

public sealed record ScanError(string ProviderId, string Location, string Message);

public sealed record ScanResult(IReadOnlyList<StartupItem> Items, IReadOnlyList<ScanError> Errors);

/// <summary>
/// Per-scan state. Providers receive their Windows abstractions through the constructor;
/// this carries only what changes between passes, which is which user hive is being read.
/// </summary>
public sealed class ScanContext
{
    /// <summary>"HKCU", or "HKU\{sid}" when reading another profile's mounted hive.</summary>
    public required string UserRoot { get; init; }

    /// <summary>SID of the user whose context the scanned entries run in. Null for the interactive HKCU pass.</summary>
    public string? UserSid { get; init; }

    public IReadOnlyList<UserProfile> Profiles { get; init; } = Array.Empty<UserProfile>();

    /// <summary>True when UserRoot points at a hive mounted by UserHiveProvider rather than at HKCU.</summary>
    public bool ScanningMountedHive { get; init; }

    /// <summary>Thread-safe sink. Providers add per-entry failures here instead of throwing.</summary>
    public ConcurrentBag<ScanError> Errors { get; } = new();
}
