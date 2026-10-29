using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Providers;

/// <summary>
/// Runs the per-user providers against every other profile's hive, so users who are not logged on
/// are still scanned. Hives are always unloaded in a finally block.
/// </summary>
public sealed class UserHiveProvider(
    IReadOnlyList<IStartupProvider> inner,
    IProfileEnumerator profiles,
    IUserHiveLoader loader) : IStartupProvider
{
    public string Id => "user-hive";

    /// <summary>Nominal only: the items yielded carry their own inner provider's category.</summary>
    public StartupCategory Category => StartupCategory.RunKey;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        foreach (var profile in profiles.Enumerate())
        {
            // The HKCU pass already covered the interactive user.
            if (profile.Sid == ctx.UserSid) continue;

            string root;
            IDisposable? lease = null;

            if (profile.HiveLoaded)
            {
                // RegLoadKey fails while the user is logged on, because the hive is already mounted.
                root = $@"HKU\{profile.Sid}";
            }
            else
            {
                MountedHive mounted;
                try
                {
                    mounted = loader.Load(profile.Sid, Path.Combine(profile.ProfilePath, "NTUSER.DAT"));
                }
                catch (Exception ex)
                {
                    ctx.Errors.Add(new ScanError(Id, profile.ProfilePath,
                        $"Could not load the profile hive for {profile.UserName ?? profile.Sid}: {ex.Message}"));
                    continue;
                }

                root = mounted.Root;
                lease = mounted.Lease;
            }

            try
            {
                var scoped = new ScanContext
                {
                    UserRoot = root,
                    UserSid = profile.Sid,
                    ScanningMountedHive = true,
                };

                foreach (var provider in inner)
                    foreach (var item in SafeScan(provider, scoped, ctx))
                        yield return item;
            }
            finally
            {
                lease?.Dispose();
            }
        }
    }

    /// <summary>Materialises one inner provider's results so a failure cannot abort the profile.</summary>
    private IReadOnlyList<StartupItem> SafeScan(IStartupProvider provider, ScanContext scoped, ScanContext outer)
    {
        try
        {
            var items = provider.Scan(scoped).ToList();
            foreach (var error in scoped.Errors) outer.Errors.Add(error);
            return items;
        }
        catch (Exception ex)
        {
            outer.Errors.Add(new ScanError(provider.Id, scoped.UserRoot, ex.Message));
            return Array.Empty<StartupItem>();
        }
    }
}
