using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class NetshHelperProviderTests
{
    private const string Netsh = @"HKLM\SOFTWARE\Microsoft\Netsh";

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var provider = new NetshHelperProvider(new FakeRegistry(), new FakeFileSystem());
        Assert.Equal("netsh-helper", provider.Id);
        Assert.Equal(StartupCategory.NetshHelper, provider.Category);
    }

    [Fact]
    public void ReportsEveryHelperDll()
    {
        var registry = new FakeRegistry();
        registry.Set(Netsh, "dhcpcmonitor", "dhcpcmonitor.dll", RegistryValueType.String);
        registry.Set(Netsh, "evil", @"C:\evil.dll", RegistryValueType.String);
        var provider = new NetshHelperProvider(registry, new FakeFileSystem());

        var items = provider.Scan(new ScanContext { UserRoot = "HKCU" }).ToList();

        Assert.Equal(2, items.Count);
        var evil = items.Single(i => i.Name == "evil");
        Assert.Equal(@"C:\evil.dll", evil.RawValue);
        Assert.Equal(Netsh, evil.Location);
        Assert.Equal(EnableState.Enabled, evil.State);
    }

    [Fact]
    public void MissingKeyYieldsNothing()
    {
        var provider = new NetshHelperProvider(new FakeRegistry(), new FakeFileSystem());
        Assert.Empty(provider.Scan(new ScanContext { UserRoot = "HKCU" }));
    }
}
