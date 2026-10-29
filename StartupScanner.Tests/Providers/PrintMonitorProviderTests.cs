using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class PrintMonitorProviderTests
{
    private const string Monitors = @"HKLM\SYSTEM\CurrentControlSet\Control\Print\Monitors";
    private const string Providers = @"HKLM\SYSTEM\CurrentControlSet\Control\Print\Providers";

    private static (PrintMonitorProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new PrintMonitorProvider(registry, new FakeFileSystem()), registry);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("print-monitor", provider.Id);
        Assert.Equal(StartupCategory.PrintMonitor, provider.Category);
    }

    [Fact]
    public void ReportsAMonitorsDriverDll()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Monitors}\Local Port", "Driver", "localspl.dll", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal("Local Port", item.Name);
        Assert.Equal($@"{Monitors}\Local Port", item.Location);
        Assert.Equal("localspl.dll", item.RawValue);
        Assert.Equal("Driver", item.Extra["ValueName"]);
    }

    [Fact]
    public void ReportsAPrintProvidersDriverDll()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Providers}\Internet Print Provider", "Driver", "inetpp.dll", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal($@"{Providers}\Internet Print Provider", item.Location);
    }

    [Fact]
    public void MonitorsWithNoDriverValueAreIgnored()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Monitors}\Weird", "Something", "x", RegistryValueType.String);

        Assert.Empty(provider.Scan(Ctx()));
    }

    [Fact]
    public void AnInaccessibleMonitorBecomesAScanError()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Monitors}\Blocked", "Driver", "x.dll", RegistryValueType.String);
        registry.Deny($@"{Monitors}\Blocked");
        var ctx = Ctx();

        Assert.Empty(provider.Scan(ctx));
        Assert.Contains(ctx.Errors, e => e.ProviderId == "print-monitor");
    }
}
