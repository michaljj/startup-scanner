using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Tests.Fakes;

public sealed class FakeProfileEnumerator(params UserProfile[] profiles) : IProfileEnumerator
{
    public IReadOnlyList<UserProfile> Enumerate() => profiles;
}

public sealed class FakeUserHiveLoader : IUserHiveLoader
{
    public List<string> Loaded { get; } = new();
    public List<string> Unloaded { get; } = new();
    public HashSet<string> FailFor { get; } = new(StringComparer.OrdinalIgnoreCase);

    public MountedHive Load(string sid, string hiveFile)
    {
        if (FailFor.Contains(sid)) throw new IOException($"cannot load {hiveFile}");

        Loaded.Add(sid);
        return new MountedHive($@"HKU\StartupScanner_{sid}", new Lease(this, sid));
    }

    private sealed class Lease(FakeUserHiveLoader owner, string sid) : IDisposable
    {
        public void Dispose() => owner.Unloaded.Add(sid);
    }
}
