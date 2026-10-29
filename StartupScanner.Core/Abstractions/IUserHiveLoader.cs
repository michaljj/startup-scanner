namespace StartupScanner.Core.Abstractions;

/// <summary>A mounted hive and the lease whose disposal unloads it.</summary>
public sealed record MountedHive(string Root, IDisposable Lease);

public interface IUserHiveLoader
{
    /// <summary>
    /// Mounts a profile's NTUSER.DAT and returns the registry root it appears under.
    /// Throws when the hive cannot be loaded — in use, missing, or access denied.
    /// </summary>
    MountedHive Load(string sid, string hiveFile);
}
