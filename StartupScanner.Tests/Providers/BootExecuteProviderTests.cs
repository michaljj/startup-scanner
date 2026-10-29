using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class BootExecuteProviderTests
{
    private const string Key = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager";

    private static (BootExecuteProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new BootExecuteProvider(registry, new FakeFileSystem()), registry);
    }

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("bootexecute", provider.Id);
        Assert.Equal(StartupCategory.BootExecute, provider.Category);
    }

    [Fact]
    public void ReadsAllFourValueNames()
    {
        var (provider, registry) = Sut();
        registry.Set(Key, "BootExecute", new[] { "autocheck autochk *" }, RegistryValueType.MultiString);
        registry.Set(Key, "SetupExecute", new[] { "setup.exe" }, RegistryValueType.MultiString);
        registry.Set(Key, "PlatformExecute", new[] { "platform.exe" }, RegistryValueType.MultiString);
        registry.Set(Key, "S0InitialCommand", "init.exe", RegistryValueType.String);

        var names = provider.Scan(new ScanContext { UserRoot = "HKCU" }).Select(i => i.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "BootExecute", "PlatformExecute", "S0InitialCommand", "SetupExecute" }, names);
    }

    [Fact]
    public void MultiStringValuesRenderJoinedSoTheGuardrailCanMatchTheDefault()
    {
        var (provider, registry) = Sut();
        registry.Set(Key, "BootExecute", new[] { "autocheck autochk *" }, RegistryValueType.MultiString);

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }));

        Assert.Equal("autocheck autochk *", item.RawValue);
        Assert.True(Guardrails.Evaluate(item).Blocked);
    }

    [Fact]
    public void AnAppendedBootExecuteEntryIsNotBlocked()
    {
        var (provider, registry) = Sut();
        registry.Set(Key, "BootExecute", new[] { "autocheck autochk *", "evil.exe" }, RegistryValueType.MultiString);

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }));

        Assert.Equal("autocheck autochk *; evil.exe", item.RawValue);
        Assert.False(Guardrails.Evaluate(item).Blocked);
    }

    [Fact]
    public void MissingKeyYieldsNothing()
    {
        var (provider, _) = Sut();
        Assert.Empty(provider.Scan(new ScanContext { UserRoot = "HKCU" }));
    }
}
