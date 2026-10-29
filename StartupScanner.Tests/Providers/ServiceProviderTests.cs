using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class ServiceProviderTests
{
    private const string Services = @"HKLM\SYSTEM\CurrentControlSet\Services";

    private static (ServiceProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new ServiceProvider(registry, new FakeFileSystem()), registry);
    }

    private static void AddService(FakeRegistry registry, string name, int start, int type,
                                   string imagePath = @"C:\Windows\System32\svchost.exe -k netsvcs")
    {
        registry.Set($@"{Services}\{name}", "Start", start, RegistryValueType.Dword);
        registry.Set($@"{Services}\{name}", "Type", type, RegistryValueType.Dword);
        registry.Set($@"{Services}\{name}", "ImagePath", imagePath, RegistryValueType.ExpandString);
    }

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("service", provider.Id);
        Assert.Equal(StartupCategory.Service, provider.Category);
    }

    [Fact]
    public void ReadsAnAutomaticServiceAsEnabled()
    {
        var (provider, registry) = Sut();
        AddService(registry, "Spooler", start: 2, type: 32);
        registry.Set($@"{Services}\Spooler", "DisplayName", "Print Spooler", RegistryValueType.String);
        registry.Set($@"{Services}\Spooler", "ObjectName", "LocalSystem", RegistryValueType.String);
        registry.Set($@"{Services}\Spooler", "Description", "Manages printing", RegistryValueType.String);
        registry.Set($@"{Services}\Spooler", "DependOnService", new[] { "RPCSS", "http" }, RegistryValueType.MultiString);

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }).Where(i => i.Name == "Spooler"));

        Assert.Equal(StartupCategory.Service, item.Category);
        Assert.Equal(EnableState.Enabled, item.State);
        Assert.Equal("Service.Start=2", item.StateSource);
        Assert.Equal("2", item.Extra["Start"]);
        Assert.Equal("32", item.Extra["Type"]);
        Assert.Equal("Print Spooler", item.Extra["DisplayName"]);
        Assert.Equal("LocalSystem", item.Extra["ObjectName"]);
        Assert.Equal("Manages printing", item.Extra["Description"]);
        Assert.Equal("RPCSS; http", item.Extra["DependOnService"]);
        Assert.Equal(@"C:\Windows\System32\svchost.exe", item.ImagePath);
        Assert.Equal("-k netsvcs", item.Arguments);
        Assert.Equal($@"{Services}\Spooler", item.Location);
    }

    [Fact]
    public void StartFourReadsAsDisabled()
    {
        var (provider, registry) = Sut();
        AddService(registry, "Disabled1", start: 4, type: 16);

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }).Where(i => i.Name == "Disabled1"));

        Assert.Equal(EnableState.Disabled, item.State);
        Assert.Equal("Service.Start=4", item.StateSource);
    }

    [Theory]
    [InlineData(1, StartupCategory.Driver)]
    [InlineData(2, StartupCategory.Driver)]
    [InlineData(4, StartupCategory.Driver)]
    [InlineData(8, StartupCategory.Driver)]
    [InlineData(16, StartupCategory.Service)]
    [InlineData(32, StartupCategory.Service)]
    [InlineData(272, StartupCategory.Service)]
    public void TypeSeparatesDriversFromServices(int type, StartupCategory expected)
    {
        var (provider, registry) = Sut();
        AddService(registry, "Thing", start: 3, type: type);

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }).Where(i => i.Name == "Thing"));

        Assert.Equal(expected, item.Category);
    }

    [Fact]
    public void ServiceWithNoStartValueIsUnknownAndPartiallyRead()
    {
        var (provider, registry) = Sut();
        registry.AddKey($@"{Services}\Weird");

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }).Where(i => i.Name == "Weird"));

        Assert.Equal(EnableState.Unknown, item.State);
        Assert.True(item.PartiallyRead);
    }

    [Fact]
    public void RawValueIsTheImagePathBecauseThatIsWhatRuns()
    {
        var (provider, registry) = Sut();
        AddService(registry, "Thing", 2, 16, @"\SystemRoot\System32\drivers\thing.sys");

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }).Where(i => i.Name == "Thing"));

        Assert.Equal(@"\SystemRoot\System32\drivers\thing.sys", item.RawValue);
    }

    [Fact]
    public void InaccessibleServiceSubkeyBecomesAScanErrorAndTheRestStillScan()
    {
        var (provider, registry) = Sut();
        AddService(registry, "Good", 2, 16);
        AddService(registry, "Protected", 2, 16);
        registry.Deny($@"{Services}\Protected");
        var ctx = new ScanContext { UserRoot = "HKCU" };

        var names = provider.Scan(ctx).Select(i => i.Name).ToList();

        Assert.Contains("Good", names);
        Assert.DoesNotContain("Protected", names);
        Assert.Contains(ctx.Errors, e => e.Location.EndsWith("Protected"));
    }

    [Fact]
    public void NoServicesKeyYieldsNothing()
    {
        var (provider, _) = Sut();
        Assert.Empty(provider.Scan(new ScanContext { UserRoot = "HKCU" }));
    }
}
