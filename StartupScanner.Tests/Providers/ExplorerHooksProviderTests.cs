using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class ExplorerHooksProviderTests
{
    private const string UserWindows = @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\Windows";
    private const string ShellExecuteHooks = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellExecuteHooks";
    private const string SharedTaskScheduler = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SharedTaskScheduler";
    private const string DelayLoad = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\ShellServiceObjectDelayLoad";
    private const string Clsid = "{AAAAAAAA-1111-2222-3333-444444444444}";

    private static (ExplorerHooksProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new ExplorerHooksProvider(registry, new FakeFileSystem()), registry);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("explorer-hooks", provider.Id);
        Assert.Equal(StartupCategory.ExplorerHook, provider.Category);
    }

    [Fact]
    public void ReadsTheLoadAndRunValuesFromThePerUserWindowsKey()
    {
        var (provider, registry) = Sut();
        registry.Set(UserWindows, "Load", @"C:\load.exe", RegistryValueType.String);
        registry.Set(UserWindows, "Run", @"C:\run.exe", RegistryValueType.String);

        var names = provider.Scan(Ctx()).Select(i => i.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "Load", "Run" }, names);
    }

    [Fact]
    public void ResolvesAShellExecuteHookClsidToItsInprocServer()
    {
        var (provider, registry) = Sut();
        registry.Set(ShellExecuteHooks, Clsid, "Some hook", RegistryValueType.String);
        registry.Set($@"HKLM\SOFTWARE\Classes\CLSID\{Clsid}\InprocServer32", "", @"C:\hook.dll", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()).Where(i => i.Name == Clsid));

        Assert.Equal(@"C:\hook.dll", item.ImagePath);
        Assert.Equal("Some hook", item.RawValue);
        Assert.Equal(Clsid, item.Extra["Clsid"]);
        Assert.Equal(ShellExecuteHooks, item.Location);
    }

    [Fact]
    public void AnUnresolvableClsidIsStillReportedAndMarkedPartiallyRead()
    {
        var (provider, registry) = Sut();
        registry.Set(ShellExecuteHooks, Clsid, "Orphan hook", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Null(item.ImagePath);
        Assert.True(item.PartiallyRead);
    }

    [Fact]
    public void CoversSharedTaskSchedulerAndShellServiceObjectDelayLoad()
    {
        var (provider, registry) = Sut();
        registry.Set(SharedTaskScheduler, Clsid, "Scheduler entry", RegistryValueType.String);
        registry.Set(DelayLoad, "WebCheck", Clsid, RegistryValueType.String);

        var locations = provider.Scan(Ctx()).Select(i => i.Location).Distinct().OrderBy(l => l).ToArray();

        // Sorted: "...\CurrentVersion\Explorer\..." precedes "...\CurrentVersion\ShellService...".
        Assert.Equal(new[] { SharedTaskScheduler, DelayLoad }, locations);
    }

    [Fact]
    public void DelayLoadEntriesResolveTheClsidFromTheValueDataNotTheValueName()
    {
        var (provider, registry) = Sut();
        registry.Set(DelayLoad, "WebCheck", Clsid, RegistryValueType.String);
        registry.Set($@"HKLM\SOFTWARE\Classes\CLSID\{Clsid}\InprocServer32", "", @"C:\webcheck.dll", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal("WebCheck", item.Name);
        Assert.Equal(@"C:\webcheck.dll", item.ImagePath);
        Assert.Equal(Clsid, item.Extra["Clsid"]);
    }
}
