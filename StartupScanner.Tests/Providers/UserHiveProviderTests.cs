using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class UserHiveProviderTests
{
    /// <summary>Records the UserRoot it was handed and yields one item tagged with it.</summary>
    private sealed class RootRecordingProvider : IStartupProvider
    {
        public List<string> Roots { get; } = new();
        public List<string?> Sids { get; } = new();

        public string Id => "registry.run";
        public StartupCategory Category => StartupCategory.RunKey;

        public IEnumerable<StartupItem> Scan(ScanContext ctx)
        {
            Roots.Add(ctx.UserRoot);
            Sids.Add(ctx.UserSid);
            yield return new StartupItem
            {
                Id = Identity.Compute(Id, ctx.UserSid, ctx.UserRoot, "Thing"),
                ProviderId = Id, Location = ctx.UserRoot, Name = "Thing", RawValue = "x.exe",
                Category = StartupCategory.RunKey, UserSid = ctx.UserSid,
            };
        }
    }

    private sealed class ThrowingInnerProvider : IStartupProvider
    {
        public string Id => "com-hijack";
        public StartupCategory Category => StartupCategory.ComHijack;
        public IEnumerable<StartupItem> Scan(ScanContext ctx) => throw new InvalidOperationException("inner blew up");
    }

    private static ScanContext Ctx(string? interactiveSid = "S-1-5-21-1")
        => new() { UserRoot = "HKCU", UserSid = interactiveSid };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var provider = new UserHiveProvider(Array.Empty<IStartupProvider>(),
            new FakeProfileEnumerator(), new FakeUserHiveLoader());

        Assert.Equal("user-hive", provider.Id);
        Assert.Equal(StartupCategory.RunKey, provider.Category);
    }

    [Fact]
    public void LoadsAndUnloadsAnOfflineProfilesHive()
    {
        var inner = new RootRecordingProvider();
        var loader = new FakeUserHiveLoader();
        var provider = new UserHiveProvider(new IStartupProvider[] { inner },
            new FakeProfileEnumerator(new UserProfile("S-1-5-21-42", @"C:\Users\bob", false, "bob")), loader);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal(new[] { "S-1-5-21-42" }, loader.Loaded);
        Assert.Equal(new[] { "S-1-5-21-42" }, loader.Unloaded);
        Assert.Equal(@"HKU\StartupScanner_S-1-5-21-42", inner.Roots.Single());
        Assert.Equal("S-1-5-21-42", item.UserSid);
    }

    [Fact]
    public void AnAlreadyLoadedHiveIsScannedInPlaceWithoutLoading()
    {
        var inner = new RootRecordingProvider();
        var loader = new FakeUserHiveLoader();
        var provider = new UserHiveProvider(new IStartupProvider[] { inner },
            new FakeProfileEnumerator(new UserProfile("S-1-5-21-7", @"C:\Users\carol", true, "carol")), loader);

        provider.Scan(Ctx()).ToList();

        Assert.Empty(loader.Loaded);
        Assert.Equal(@"HKU\S-1-5-21-7", inner.Roots.Single());
    }

    [Fact]
    public void TheInteractiveUsersOwnProfileIsSkippedBecauseHkcuAlreadyCoveredIt()
    {
        var inner = new RootRecordingProvider();
        var provider = new UserHiveProvider(new IStartupProvider[] { inner },
            new FakeProfileEnumerator(new UserProfile("S-1-5-21-1", @"C:\Users\alice", true, "alice")),
            new FakeUserHiveLoader());

        Assert.Empty(provider.Scan(Ctx("S-1-5-21-1")));
        Assert.Empty(inner.Roots);
    }

    [Fact]
    public void AHiveThatCannotBeLoadedIsSkippedAndTheUserIsNamed()
    {
        var loader = new FakeUserHiveLoader();
        loader.FailFor.Add("S-1-5-21-99");
        var provider = new UserHiveProvider(new IStartupProvider[] { new RootRecordingProvider() },
            new FakeProfileEnumerator(new UserProfile("S-1-5-21-99", @"C:\Users\dave", false, "dave")), loader);
        var ctx = Ctx();

        Assert.Empty(provider.Scan(ctx));

        var error = Assert.Single(ctx.Errors);
        Assert.Contains("dave", error.Message);
        Assert.Empty(loader.Unloaded);
    }

    [Fact]
    public void AnInnerProviderThrowingStillUnloadsTheHiveAndRecordsTheError()
    {
        var loader = new FakeUserHiveLoader();
        var provider = new UserHiveProvider(new IStartupProvider[] { new ThrowingInnerProvider() },
            new FakeProfileEnumerator(new UserProfile("S-1-5-21-42", @"C:\Users\bob", false, "bob")), loader);
        var ctx = Ctx();

        Assert.Empty(provider.Scan(ctx));

        Assert.Equal(new[] { "S-1-5-21-42" }, loader.Unloaded);
        Assert.Contains(ctx.Errors, e => e.Message.Contains("inner blew up"));
    }

    [Fact]
    public void EveryProfileIsVisitedAndEachGetsItsOwnItemIdentity()
    {
        var inner = new RootRecordingProvider();
        var provider = new UserHiveProvider(new IStartupProvider[] { inner },
            new FakeProfileEnumerator(
                new UserProfile("S-1-5-21-42", @"C:\Users\bob", false, "bob"),
                new UserProfile("S-1-5-21-43", @"C:\Users\carol", true, "carol")),
            new FakeUserHiveLoader());

        var ids = provider.Scan(Ctx()).Select(i => i.Id).Distinct().ToList();

        Assert.Equal(2, ids.Count);
        Assert.Equal(new[] { "S-1-5-21-42", "S-1-5-21-43" }, inner.Sids);
    }

    [Fact]
    public void TheMountedContextIsFlaggedSoInnerProvidersKnow()
    {
        var flags = new List<bool>();
        var recorder = new FlagRecordingProvider(flags);
        var provider = new UserHiveProvider(new IStartupProvider[] { recorder },
            new FakeProfileEnumerator(new UserProfile("S-1-5-21-42", @"C:\Users\bob", false, "bob")),
            new FakeUserHiveLoader());

        provider.Scan(Ctx()).ToList();

        Assert.True(Assert.Single(flags));
    }

    private sealed class FlagRecordingProvider(List<bool> flags) : IStartupProvider
    {
        public string Id => "registry.run";
        public StartupCategory Category => StartupCategory.RunKey;

        public IEnumerable<StartupItem> Scan(ScanContext ctx)
        {
            flags.Add(ctx.ScanningMountedHive);
            return Array.Empty<StartupItem>();
        }
    }
}
