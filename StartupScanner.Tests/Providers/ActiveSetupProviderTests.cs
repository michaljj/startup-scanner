using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class ActiveSetupProviderTests
{
    private const string Machine = @"HKLM\SOFTWARE\Microsoft\Active Setup\Installed Components";
    private const string User = @"HKCU\Software\Microsoft\Active Setup\Installed Components";
    private const string Guid = "{89820200-ECBD-11cf-8B85-00AA005B4383}";

    private static (ActiveSetupProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new ActiveSetupProvider(registry, new FakeFileSystem()), registry);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("active-setup", provider.Id);
        Assert.Equal(StartupCategory.ActiveSetup, provider.Category);
    }

    [Fact]
    public void ReportsAComponentWithAStubPath()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Machine}\{Guid}", "StubPath", @"C:\Windows\System32\ie4uinit.exe -BaseSettings", RegistryValueType.String);
        registry.Set($@"{Machine}\{Guid}", "Version", "27,1,0,0", RegistryValueType.String);
        registry.Set($@"{Machine}\{Guid}", "ComponentID", "IE", RegistryValueType.String);
        registry.Set($@"{Machine}\{Guid}", "IsInstalled", 1, RegistryValueType.Dword);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal(Guid, item.Name);
        Assert.Equal($@"{Machine}\{Guid}", item.Location);
        Assert.Equal(@"C:\Windows\System32\ie4uinit.exe -BaseSettings", item.RawValue);
        Assert.Equal(@"C:\Windows\System32\ie4uinit.exe", item.ImagePath);
        Assert.Equal("-BaseSettings", item.Arguments);
        Assert.Equal("27,1,0,0", item.Extra["MachineVersion"]);
        Assert.Equal("IE", item.Extra["ComponentId"]);
        Assert.Equal("1", item.Extra["IsInstalled"]);
    }

    [Fact]
    public void ComponentsWithNoStubPathAreNotAutostartEntries()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Machine}\{Guid}", "Version", "1,0,0,0", RegistryValueType.String);

        Assert.Empty(provider.Scan(Ctx()));
    }

    [Fact]
    public void AMissingUserStampMeansTheStubWillRunAtNextLogon()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Machine}\{Guid}", "StubPath", "run.exe", RegistryValueType.String);
        registry.Set($@"{Machine}\{Guid}", "Version", "27,1,0,0", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal("", item.Extra["UserVersion"]);
        Assert.Equal("True", item.Extra["WillRunAtNextLogon"]);
    }

    [Fact]
    public void AMatchingUserStampMeansTheStubAlreadyRan()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Machine}\{Guid}", "StubPath", "run.exe", RegistryValueType.String);
        registry.Set($@"{Machine}\{Guid}", "Version", "27,1,0,0", RegistryValueType.String);
        registry.Set($@"{User}\{Guid}", "Version", "27,1,0,0", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal("27,1,0,0", item.Extra["UserVersion"]);
        Assert.Equal("False", item.Extra["WillRunAtNextLogon"]);
    }

    [Fact]
    public void AStaleUserStampMeansTheStubWillRunAgain()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Machine}\{Guid}", "StubPath", "run.exe", RegistryValueType.String);
        registry.Set($@"{Machine}\{Guid}", "Version", "27,1,0,0", RegistryValueType.String);
        registry.Set($@"{User}\{Guid}", "Version", "26,0,0,0", RegistryValueType.String);

        Assert.Equal("True", Assert.Single(provider.Scan(Ctx())).Extra["WillRunAtNextLogon"]);
    }

    [Fact]
    public void AnInaccessibleComponentBecomesAScanError()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Machine}\{Guid}", "StubPath", "run.exe", RegistryValueType.String);
        registry.Deny($@"{Machine}\{Guid}");
        var ctx = Ctx();

        Assert.Empty(provider.Scan(ctx));
        Assert.Contains(ctx.Errors, e => e.ProviderId == "active-setup");
    }
}
