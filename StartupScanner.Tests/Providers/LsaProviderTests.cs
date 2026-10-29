using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class LsaProviderTests
{
    private const string Lsa = @"HKLM\SYSTEM\CurrentControlSet\Control\Lsa";

    private static (LsaProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new LsaProvider(registry, new FakeFileSystem()), registry);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("lsa", provider.Id);
        Assert.Equal(StartupCategory.Lsa, provider.Category);
    }

    [Fact]
    public void ReadsTheThreePackageLists()
    {
        var (provider, registry) = Sut();
        registry.Set(Lsa, "Authentication Packages", new[] { "msv1_0" }, RegistryValueType.MultiString);
        registry.Set(Lsa, "Notification Packages", new[] { "scecli" }, RegistryValueType.MultiString);
        registry.Set(Lsa, "Security Packages", new[] { "kerberos", "msv1_0" }, RegistryValueType.MultiString);

        var names = provider.Scan(Ctx()).Select(i => i.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "Authentication Packages", "Notification Packages", "Security Packages" }, names);
    }

    [Fact]
    public void MultiStringPackageListsRenderJoined()
    {
        var (provider, registry) = Sut();
        registry.Set(Lsa, "Security Packages", new[] { "kerberos", "msv1_0" }, RegistryValueType.MultiString);

        Assert.Equal("kerberos; msv1_0", Assert.Single(provider.Scan(Ctx())).RawValue);
    }

    [Fact]
    public void AlsoReadsTheOsConfigCopy()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Lsa}\OSConfig", "Security Packages", new[] { "kerberos" }, RegistryValueType.MultiString);

        Assert.Equal($@"{Lsa}\OSConfig", Assert.Single(provider.Scan(Ctx())).Location);
    }

    [Fact]
    public void EveryLsaItemIsBlockedAndCriticalRisk()
    {
        var (provider, registry) = Sut();
        registry.Set(Lsa, "Authentication Packages", new[] { "msv1_0" }, RegistryValueType.MultiString);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.True(Guardrails.Evaluate(item).Blocked);
        Assert.Equal(RiskTier.Critical, RiskTiers.For(item));
        Assert.Equal(ToggleCapability.Blocked, new BlockedToggle("lsa").GetCapability(item));
    }

    [Fact]
    public void TheBlockedToggleRefusesToWriteEvenIfCalledDirectly()
    {
        var toggle = new BlockedToggle("lsa");
        var item = new StartupItem
        {
            Id = "id", ProviderId = "lsa", Location = Lsa, Name = "Security Packages",
            RawValue = "kerberos", Category = StartupCategory.Lsa,
        };

        Assert.Throws<InvalidOperationException>(() => toggle.SetEnabled(item, false, new FakeJournal()));
    }
}
