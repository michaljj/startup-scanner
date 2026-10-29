using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class IfeoProviderTests
{
    private const string Ifeo = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    private static (IfeoProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new IfeoProvider(registry, new FakeFileSystem()), registry);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("ifeo", provider.Id);
        Assert.Equal(StartupCategory.Ifeo, provider.Category);
    }

    [Fact]
    public void ReportsADebuggerHijack()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Ifeo}\sethc.exe", "Debugger", @"C:\Windows\System32\cmd.exe",
                     RegistryValueType.String, RegistryViewKind.Registry64);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal("sethc.exe:Debugger", item.Name);
        Assert.Equal($@"{Ifeo}\sethc.exe", item.Location);
        Assert.Equal(@"C:\Windows\System32\cmd.exe", item.RawValue);
        Assert.Equal("sethc.exe", item.Extra["TargetImage"]);
        Assert.Equal("Debugger", item.Extra["ValueName"]);
        Assert.Equal(RiskTier.High, RiskTiers.For(item));
    }

    [Fact]
    public void OneImageWithSeveralInterestingValuesProducesSeveralItems()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Ifeo}\app.exe", "Debugger", "dbg.exe", RegistryValueType.String, RegistryViewKind.Registry64);
        registry.Set($@"{Ifeo}\app.exe", "VerifierDlls", "verify.dll", RegistryValueType.String, RegistryViewKind.Registry64);
        registry.Set($@"{Ifeo}\app.exe", "GlobalFlag", "0x100", RegistryValueType.String, RegistryViewKind.Registry64);

        var names = provider.Scan(Ctx()).Select(i => i.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "app.exe:Debugger", "app.exe:GlobalFlag", "app.exe:VerifierDlls" }, names);
    }

    [Fact]
    public void IdentitiesForDifferentValuesOfTheSameImageDiffer()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Ifeo}\app.exe", "Debugger", "dbg.exe", RegistryValueType.String, RegistryViewKind.Registry64);
        registry.Set($@"{Ifeo}\app.exe", "VerifierDlls", "v.dll", RegistryValueType.String, RegistryViewKind.Registry64);

        Assert.Equal(2, provider.Scan(Ctx()).Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public void SubkeysWithNoInterestingValuesAreIgnored()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Ifeo}\notepad.exe", "MitigationOptions", "0", RegistryValueType.String, RegistryViewKind.Registry64);

        Assert.Empty(provider.Scan(Ctx()));
    }

    [Fact]
    public void BothViewsAreWalked()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Ifeo}\a.exe", "Debugger", "x64.exe", RegistryValueType.String, RegistryViewKind.Registry64);
        registry.Set($@"{Ifeo}\b.exe", "Debugger", "x86.exe", RegistryValueType.String, RegistryViewKind.Registry32);

        Assert.Equal(2, provider.Scan(Ctx()).Count());
    }
}
