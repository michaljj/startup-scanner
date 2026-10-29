using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class AppInitProviderTests
{
    private const string Windows = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows";
    private const string AppCert = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\AppCertDlls";

    private static (AppInitProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new AppInitProvider(registry, new FakeFileSystem()), registry);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("appinit", provider.Id);
        Assert.Equal(StartupCategory.AppInit, provider.Category);
    }

    [Fact]
    public void ReadsAppInitDllsAndItsEnableFlag()
    {
        var (provider, registry) = Sut();
        registry.Set(Windows, "AppInit_DLLs", @"C:\hook.dll", RegistryValueType.String, RegistryViewKind.Registry64);
        registry.Set(Windows, "LoadAppInit_DLLs", 1, RegistryValueType.Dword, RegistryViewKind.Registry64);

        var names = provider.Scan(Ctx()).Select(i => i.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "AppInit_DLLs", "LoadAppInit_DLLs" }, names);
    }

    [Fact]
    public void ScansBothRegistryViewsBecauseTheyHoldSeparateValues()
    {
        var (provider, registry) = Sut();
        registry.Set(Windows, "AppInit_DLLs", @"C:\hook64.dll", RegistryValueType.String, RegistryViewKind.Registry64);
        registry.Set(Windows, "AppInit_DLLs", @"C:\hook32.dll", RegistryValueType.String, RegistryViewKind.Registry32);

        var raws = provider.Scan(Ctx()).Select(i => i.RawValue).OrderBy(r => r).ToArray();

        Assert.Equal(new[] { @"C:\hook32.dll", @"C:\hook64.dll" }, raws);
    }

    [Fact]
    public void AppInitDllsIsHighRiskAndAppCertDllsIsMedium()
    {
        var (provider, registry) = Sut();
        registry.Set(Windows, "AppInit_DLLs", @"C:\hook.dll", RegistryValueType.String, RegistryViewKind.Registry64);
        registry.Set(AppCert, "Something", @"C:\cert.dll", RegistryValueType.String);

        var items = provider.Scan(Ctx()).ToList();

        Assert.Equal(RiskTier.High, RiskTiers.For(items.Single(i => i.Name == "AppInit_DLLs")));
        Assert.Equal(RiskTier.Medium, RiskTiers.For(items.Single(i => i.Name == "Something")));
    }

    [Fact]
    public void ReadsEveryValueUnderAppCertDlls()
    {
        var (provider, registry) = Sut();
        registry.Set(AppCert, "First", @"C:\a.dll", RegistryValueType.String);
        registry.Set(AppCert, "Second", @"C:\b.dll", RegistryValueType.String);

        Assert.Equal(2, provider.Scan(Ctx()).Count());
    }
}
