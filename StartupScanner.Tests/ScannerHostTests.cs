using StartupScanner.Core;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Storage;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests;

public class ScannerHostTests
{
    private static readonly string[] ExpectedProviderIds =
    {
        "registry.run", "startup.folder", "service", "task-scheduler", "winlogon", "bootexecute",
        "active-setup", "ifeo", "appinit", "explorer-hooks", "appx-startup", "wmi-subscription",
        "com-hijack", "lsa", "netsh-helper", "print-monitor", "winsock-lsp", "gpo-script", "user-hive",
    };

    private static IReadOnlyList<IStartupProvider> Providers()
        => ScannerHost.BuildProviders(new FakeRegistry(), new FakeFileSystem(), new FakeTaskScheduler(),
                                      new FakeWmi(), new FakeShellLinkResolver(), new FakeProfileEnumerator(),
                                      new FakeUserHiveLoader());

    private static IReadOnlyList<IToggleProvider> Toggles()
        => ScannerHost.BuildToggles(new FakeRegistry(), new FakeFileSystem(), new FakeTaskScheduler(),
                                    new FakeWmi(), new FakeClock());

    [Fact]
    public void AllNineteenProvidersAreRegistered()
    {
        var ids = Providers().Select(p => p.Id).ToList();

        Assert.Equal(19, ids.Count);
        foreach (var expected in ExpectedProviderIds) Assert.Contains(expected, ids);
    }

    [Fact]
    public void ProviderIdsAreUnique()
    {
        var ids = Providers().Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void EveryProviderExceptTheHiveWrapperHasExactlyOneToggle()
    {
        var toggleIds = Toggles().Select(t => t.ProviderId).ToList();

        foreach (var providerId in ExpectedProviderIds.Where(id => id != "user-hive"))
            Assert.Single(toggleIds.Where(id => id == providerId));
    }

    [Fact]
    public void TheHiveWrapperHasNoToggleBecauseItsItemsCarryInnerProviderIds()
    {
        Assert.DoesNotContain("user-hive", Toggles().Select(t => t.ProviderId));
    }

    [Fact]
    public void LsaIsWiredToTheBlockedToggle()
    {
        Assert.IsType<BlockedToggle>(Toggles().Single(t => t.ProviderId == "lsa"));
    }

    [Fact]
    public void CompositeNameProvidersAreWiredToTheSubValueToggle()
    {
        var toggles = Toggles();

        foreach (var providerId in new[] { "ifeo", "explorer-hooks", "com-hijack", "print-monitor", "winsock-lsp", "active-setup" })
            Assert.IsType<SubValueToggle>(toggles.Single(t => t.ProviderId == providerId));
    }

    [Fact]
    public void TheHiveWrapperWrapsExactlyProvidersOneSevenTenElevenAndThirteen()
    {
        var wrapper = Providers().OfType<UserHiveProvider>().Single();

        Assert.Equal(new[] { "active-setup", "appx-startup", "com-hijack", "explorer-hooks", "registry.run" },
            ScannerHost.HiveWrappedProviderIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Equal("user-hive", wrapper.Id);
    }

    [Fact]
    public void ContextForAnItemWithNoSidTargetsHkcu()
    {
        var host = ScannerHost.CreateForTesting(new FakeRegistry(), new FakeFileSystem(), new FakeTaskScheduler(),
            new FakeWmi(), new FakeShellLinkResolver(), new FakeProfileEnumerator(), new FakeUserHiveLoader(),
            new FakeSystemRestore(), new StubRestorePointPrompt(true), new RecordingConfirmationSink(),
            new FakeClock(), new AppPaths(@"C:\Data\StartupScanner"), interactiveUserSid: "S-1-5-21-1");

        Assert.Equal("HKCU", host.ContextFor().UserRoot);
        Assert.Equal("HKCU", host.ContextFor(Item(null)).UserRoot);
        Assert.Equal("HKCU", host.ContextFor(Item("S-1-5-21-1")).UserRoot);
    }

    [Fact]
    public void ContextForAnotherUsersItemTargetsThatUsersHive()
    {
        var host = ScannerHost.CreateForTesting(new FakeRegistry(), new FakeFileSystem(), new FakeTaskScheduler(),
            new FakeWmi(), new FakeShellLinkResolver(), new FakeProfileEnumerator(), new FakeUserHiveLoader(),
            new FakeSystemRestore(), new StubRestorePointPrompt(true), new RecordingConfirmationSink(),
            new FakeClock(), new AppPaths(@"C:\Data\StartupScanner"), interactiveUserSid: "S-1-5-21-1");

        var context = host.ContextFor(Item("S-1-5-21-42"));

        Assert.Equal(@"HKU\S-1-5-21-42", context.UserRoot);
        Assert.Equal("S-1-5-21-42", context.UserSid);
        Assert.True(context.ScanningMountedHive);
    }

    [Fact]
    public void SessionIdIsAFreshValuePerHost()
    {
        var first = NewHost().SessionId;
        var second = NewHost().SessionId;

        Assert.NotEqual(first, second);
        Assert.NotEmpty(first);
    }

    private static ScannerHost NewHost()
        => ScannerHost.CreateForTesting(new FakeRegistry(), new FakeFileSystem(), new FakeTaskScheduler(),
            new FakeWmi(), new FakeShellLinkResolver(), new FakeProfileEnumerator(), new FakeUserHiveLoader(),
            new FakeSystemRestore(), new StubRestorePointPrompt(true), new RecordingConfirmationSink(),
            new FakeClock(), new AppPaths(@"C:\Data\StartupScanner"), interactiveUserSid: "S-1-5-21-1");

    private static StartupItem Item(string? sid) => new()
    {
        Id = "id", ProviderId = "registry.run", Location = @"HKCU\Run", Name = "Thing",
        RawValue = "x.exe", Category = StartupCategory.RunKey, UserSid = sid,
    };
}
