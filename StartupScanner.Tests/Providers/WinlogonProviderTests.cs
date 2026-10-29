using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class WinlogonProviderTests
{
    private const string Machine = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string User = @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon";

    private static (WinlogonProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        var fs = new FakeFileSystem();
        fs.SetEnvironment("systemroot", @"C:\Windows");
        return (new WinlogonProvider(registry, fs), registry);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("winlogon", provider.Id);
        Assert.Equal(StartupCategory.Winlogon, provider.Category);
    }

    [Fact]
    public void ReadsTheMachineWinlogonValues()
    {
        var (provider, registry) = Sut();
        registry.Set(Machine, "Userinit", @"%systemroot%\system32\userinit.exe,", RegistryValueType.ExpandString);
        registry.Set(Machine, "Shell", "explorer.exe", RegistryValueType.String);
        registry.Set(Machine, "Taskman", @"C:\evil\taskman.exe", RegistryValueType.String);
        registry.Set(Machine, "AppSetup", "setup.exe", RegistryValueType.String);
        registry.Set(Machine, "GinaDLL", "gina.dll", RegistryValueType.String);

        var names = provider.Scan(Ctx()).Select(i => i.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "AppSetup", "GinaDLL", "Shell", "Taskman", "Userinit" }, names);
    }

    [Fact]
    public void ExpandsEnvironmentVariablesIntoImagePath()
    {
        var (provider, registry) = Sut();
        registry.Set(Machine, "Userinit", @"%systemroot%\system32\userinit.exe,", RegistryValueType.ExpandString);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal(@"%systemroot%\system32\userinit.exe,", item.RawValue);
        Assert.Equal(@"C:\Windows\system32\userinit.exe", item.ImagePath);
        Assert.Equal(EnableState.Enabled, item.State);
    }

    [Fact]
    public void BlankAndAbsentValuesAreNotReported()
    {
        var (provider, registry) = Sut();
        registry.Set(Machine, "Taskman", "", RegistryValueType.String);

        Assert.Empty(provider.Scan(Ctx()));
    }

    [Fact]
    public void ReadsThePerUserWinlogonValuesToo()
    {
        var (provider, registry) = Sut();
        registry.Set(User, "Shell", "custom.exe", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal(User, item.Location);
    }

    [Fact]
    public void WalksTheNotifySubkeysAndReportsTheirDllName()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Machine}\Notify\mynotify", "DLLName", @"C:\notify.dll", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal("mynotify", item.Name);
        Assert.Equal($@"{Machine}\Notify\mynotify", item.Location);
        Assert.Equal(@"C:\notify.dll", item.RawValue);
    }

    [Fact]
    public void AnInaccessibleKeyBecomesAScanError()
    {
        var (provider, registry) = Sut();
        registry.Set(Machine, "Shell", "explorer.exe", RegistryValueType.String);
        registry.Deny(Machine);
        var ctx = Ctx();

        Assert.Empty(provider.Scan(ctx));
        Assert.Contains(ctx.Errors, e => e.ProviderId == "winlogon");
    }
}
