using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class ComHijackProviderTests
{
    private const string Clsid = "{0006F045-0000-0000-C000-000000000046}";
    private const string UserClsids = @"HKCU\Software\Classes\CLSID";
    private const string MachineClsids = @"HKLM\SOFTWARE\Classes\CLSID";

    private static (ComHijackProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new ComHijackProvider(registry, new FakeFileSystem()), registry);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU", UserSid = "S-1-5-21-9" };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("com-hijack", provider.Id);
        Assert.Equal(StartupCategory.ComHijack, provider.Category);
    }

    [Fact]
    public void ReportsAPerUserInprocServerThatShadowsAMachineRegistration()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{UserClsids}\{Clsid}\InprocServer32", "", @"C:\Users\alice\evil.dll", RegistryValueType.String);
        registry.Set($@"{MachineClsids}\{Clsid}\InprocServer32", "", @"C:\Windows\System32\real.dll", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal(Clsid, item.Name);
        Assert.Equal($@"{UserClsids}\{Clsid}\InprocServer32", item.Location);
        Assert.Equal(@"C:\Users\alice\evil.dll", item.RawValue);
        Assert.Equal(@"C:\Users\alice\evil.dll", item.ImagePath);
        Assert.Equal("InprocServer32", item.Extra["ValueName"]);
        Assert.Equal(@"C:\Windows\System32\real.dll", item.Extra["MachineRegistration"]);
        Assert.Equal("S-1-5-21-9", item.UserSid);
    }

    [Fact]
    public void APerUserOnlyClsidIsNotAHijack()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{UserClsids}\{Clsid}\InprocServer32", "", @"C:\ownapp.dll", RegistryValueType.String);

        Assert.Empty(provider.Scan(Ctx()));
    }

    [Fact]
    public void ReportsATreatAsRedirection()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{UserClsids}\{Clsid}", "TreatAs", "{11111111-2222-3333-4444-555555555555}", RegistryValueType.String);
        registry.Set($@"{MachineClsids}\{Clsid}\InprocServer32", "", @"C:\Windows\System32\real.dll", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal("TreatAs", item.Extra["ValueName"]);
        Assert.Equal("{11111111-2222-3333-4444-555555555555}", item.RawValue);
    }

    [Fact]
    public void ClsidsWithNeitherInprocServerNorTreatAsAreIgnored()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{UserClsids}\{Clsid}", "SomethingElse", "x", RegistryValueType.String);
        registry.Set($@"{MachineClsids}\{Clsid}\InprocServer32", "", @"C:\real.dll", RegistryValueType.String);

        Assert.Empty(provider.Scan(Ctx()));
    }

    [Fact]
    public void FollowsUserRootSoAMountedHiveIsScanned()
    {
        var (provider, registry) = Sut();
        registry.Set($@"HKU\S-1-5-21-42\Software\Classes\CLSID\{Clsid}\InprocServer32", "", @"C:\evil.dll", RegistryValueType.String);
        registry.Set($@"{MachineClsids}\{Clsid}\InprocServer32", "", @"C:\real.dll", RegistryValueType.String);

        var ctx = new ScanContext { UserRoot = @"HKU\S-1-5-21-42", UserSid = "S-1-5-21-42", ScanningMountedHive = true };
        var item = Assert.Single(provider.Scan(ctx));

        Assert.Equal("S-1-5-21-42", item.UserSid);
    }

    [Fact]
    public void AnInaccessibleClsidBecomesAScanError()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{UserClsids}\{Clsid}\InprocServer32", "", @"C:\evil.dll", RegistryValueType.String);
        registry.Deny($@"{UserClsids}\{Clsid}");
        var ctx = Ctx();

        Assert.Empty(provider.Scan(ctx));
        Assert.Contains(ctx.Errors, e => e.ProviderId == "com-hijack");
    }
}
