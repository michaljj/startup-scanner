using StartupScanner.Core.Models;

namespace StartupScanner.Core.Abstractions;

public interface IProfileEnumerator
{
    /// <summary>
    /// Every local profile from ProfileList, with HiveLoaded set when HKU\&lt;SID&gt; already exists.
    /// </summary>
    IReadOnlyList<UserProfile> Enumerate();
}
