using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Windows;

public sealed class WindowsProfileEnumerator(IRegistry registry) : IProfileEnumerator
{
    private const string ProfileList = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public IReadOnlyList<UserProfile> Enumerate()
    {
        using var root = registry.OpenKey(ProfileList);
        if (root is null) return Array.Empty<UserProfile>();

        var profiles = new List<UserProfile>();

        foreach (var sid in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(sid);
            if (key is null) continue;

            var profilePath = RegistryValueCodec.ToDisplay(
                key.GetValue("ProfileImagePath"), key.GetValueType("ProfileImagePath"));
            if (string.IsNullOrWhiteSpace(profilePath)) continue;

            // A loaded hive already appears under HKU\<SID>; RegLoadKey would fail for it.
            using var mounted = registry.OpenKey($@"HKU\{sid}");

            profiles.Add(new UserProfile(sid, Environment.ExpandEnvironmentVariables(profilePath),
                                         mounted is not null, Path.GetFileName(profilePath.TrimEnd('\\'))));
        }

        return profiles;
    }
}
