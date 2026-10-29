using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Windows;

namespace StartupScanner.Tests.Windows;

public class WindowsRegistryTests
{
    private const string CurrentVersion = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    [Fact]
    public void ReadsARealHklmValue()
    {
        using var key = new WindowsRegistry().OpenKey(CurrentVersion);

        Assert.NotNull(key);
        Assert.False(string.IsNullOrEmpty(key!.GetValue("ProductName") as string));
        Assert.Equal(RegistryValueType.String, key.GetValueType("ProductName"));
    }

    [Fact]
    public void EnumeratesRealSubKeys()
    {
        using var key = new WindowsRegistry().OpenKey(@"HKLM\SYSTEM\CurrentControlSet\Services");

        Assert.NotNull(key);
        Assert.NotEmpty(key!.GetSubKeyNames());
    }

    [Fact]
    public void ReadsHkcu()
    {
        using var key = new WindowsRegistry().OpenKey(@"HKCU\Software");

        Assert.NotNull(key);
    }

    [Fact]
    public void MissingKeyReturnsNull()
    {
        Assert.Null(new WindowsRegistry().OpenKey(@"HKLM\SOFTWARE\NoSuchKeyStartupScanner"));
    }

    [Fact]
    public void UnknownRootThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new WindowsRegistry().OpenKey(@"HKXX\Whatever"));
    }

    [Fact]
    public void ThirtyTwoAndSixtyFourBitViewsBothOpen()
    {
        var registry = new WindowsRegistry();

        using var wide = registry.OpenKey(CurrentVersion, RegistryViewKind.Registry64);
        using var narrow = registry.OpenKey(CurrentVersion, RegistryViewKind.Registry32);

        Assert.NotNull(wide);
        Assert.NotNull(narrow);
    }

    [Fact]
    public void HkuPathsResolveAgainstTheUsersHive()
    {
        using var key = new WindowsRegistry().OpenKey(@"HKU\.DEFAULT");

        Assert.NotNull(key);
    }

    [Fact]
    public void ProfileEnumeratorFindsAtLeastTheSystemProfiles()
    {
        var profiles = new WindowsProfileEnumerator(new WindowsRegistry()).Enumerate();

        Assert.NotEmpty(profiles);
        Assert.All(profiles, p => Assert.StartsWith("S-1-", p.Sid));
        Assert.All(profiles, p => Assert.False(string.IsNullOrWhiteSpace(p.ProfilePath)));
    }

    [Fact]
    public void FileSystemExpandsEnvironmentVariablesAndStatsARealFile()
    {
        var fs = new WindowsFileSystem();
        var path = fs.ExpandEnvironment(@"%SystemRoot%\explorer.exe");

        Assert.StartsWith(@"C:\", path, StringComparison.OrdinalIgnoreCase);
        Assert.True(fs.FileExists(path));
        Assert.True(fs.Stat(path)!.Size > 0);
    }
}
