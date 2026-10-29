using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class StartupFolderProviderTests
{
    private const string UserStartup = @"C:\Users\alice\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup";
    private const string CommonStartup = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup";
    private const string ApprovedUser = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    private static (StartupFolderProvider Provider, FakeFileSystem Fs, FakeRegistry Registry, FakeShellLinkResolver Links) Sut()
    {
        var fs = new FakeFileSystem();
        fs.SetEnvironment("APPDATA", @"C:\Users\alice\AppData\Roaming");
        fs.SetEnvironment("ProgramData", @"C:\ProgramData");
        var registry = new FakeRegistry();
        var links = new FakeShellLinkResolver();
        return (new StartupFolderProvider(fs, registry, links), fs, registry, links);
    }

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _, _, _) = Sut();
        Assert.Equal("startup.folder", provider.Id);
        Assert.Equal(StartupCategory.StartupFolder, provider.Category);
    }

    [Fact]
    public void ResolvesAShortcutIntoImagePathArgumentsAndWorkingDirectory()
    {
        var (provider, fs, _, links) = Sut();
        var lnk = Path.Combine(UserStartup, "Spotify.lnk");
        fs.AddFile(lnk, "shortcut");
        links.Add(lnk, new ShortcutTarget(@"C:\Spotify\spotify.exe", "--minimized", @"C:\Spotify", @"C:\Spotify\app.ico"));

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }).Where(i => i.Name == "Spotify.lnk"));

        Assert.Equal(@"C:\Spotify\spotify.exe", item.ImagePath);
        Assert.Equal("--minimized", item.Arguments);
        Assert.Equal(@"C:\Spotify", item.Extra["WorkingDirectory"]);
        Assert.Equal(@"C:\Spotify\app.ico", item.Extra["IconLocation"]);
        Assert.Equal(UserStartup, item.Location);
    }

    [Fact]
    public void ANonShortcutFileIsStillReportedWithItselfAsTheImage()
    {
        var (provider, fs, _, _) = Sut();
        fs.AddFile(Path.Combine(UserStartup, "cleanup.bat"), "echo hi");

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }).Where(i => i.Name == "cleanup.bat"));

        Assert.Equal(Path.Combine(UserStartup, "cleanup.bat"), item.ImagePath);
        Assert.Null(item.Arguments);
    }

    [Fact]
    public void AnUnresolvableShortcutIsMarkedPartiallyRead()
    {
        var (provider, fs, _, _) = Sut();
        fs.AddFile(Path.Combine(UserStartup, "Broken.lnk"), "junk");

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }).Where(i => i.Name == "Broken.lnk"));

        Assert.True(item.PartiallyRead);
        Assert.Null(item.ImagePath);
    }

    [Fact]
    public void ScansTheCommonStartupFolderToo()
    {
        var (provider, fs, _, links) = Sut();
        var lnk = Path.Combine(CommonStartup, "Everyone.lnk");
        fs.AddFile(lnk, "shortcut");
        links.Add(lnk, new ShortcutTarget(@"C:\tool.exe", "", "", ""));

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }).Where(i => i.Name == "Everyone.lnk"));

        Assert.Equal(CommonStartup, item.Location);
        Assert.Equal("True", item.Extra["CommonFolder"]);
    }

    [Fact]
    public void ScansEveryProfileListedInTheContext()
    {
        var (provider, fs, _, links) = Sut();
        var otherStartup = @"C:\Users\bob\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup";
        var lnk = Path.Combine(otherStartup, "BobThing.lnk");
        fs.AddFile(lnk, "shortcut");
        links.Add(lnk, new ShortcutTarget(@"C:\bob.exe", "", "", ""));

        var ctx = new ScanContext
        {
            UserRoot = "HKCU",
            Profiles = new[] { new UserProfile("S-1-5-21-42", @"C:\Users\bob", false, "bob") },
        };

        var item = Assert.Single(provider.Scan(ctx).Where(i => i.Name == "BobThing.lnk"));

        Assert.Equal("S-1-5-21-42", item.UserSid);
        Assert.Equal(otherStartup, item.Location);
        Assert.Equal(@"HKU\S-1-5-21-42", item.Extra["ApprovedRoot"]);
    }

    [Fact]
    public void DisabledStartupApprovedBlobKeyedByFileNameMakesTheItemDisabled()
    {
        var (provider, fs, registry, links) = Sut();
        var lnk = Path.Combine(UserStartup, "Spotify.lnk");
        fs.AddFile(lnk, "shortcut");
        links.Add(lnk, new ShortcutTarget(@"C:\spotify.exe", "", "", ""));
        registry.Set(ApprovedUser, "Spotify.lnk",
            StartupApprovedBlob.Encode(false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc), null),
            RegistryValueType.Binary);

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }).Where(i => i.Name == "Spotify.lnk"));

        Assert.Equal(EnableState.Disabled, item.State);
        Assert.Equal("StartupApproved", item.StateSource);
    }

    [Fact]
    public void MissingFolderYieldsNothingAndNoError()
    {
        var (provider, _, _, _) = Sut();
        var ctx = new ScanContext { UserRoot = "HKCU" };

        Assert.Empty(provider.Scan(ctx));
        Assert.Empty(ctx.Errors);
    }

    [Fact]
    public void DesktopIniIsIgnored()
    {
        var (provider, fs, _, _) = Sut();
        fs.AddFile(Path.Combine(UserStartup, "desktop.ini"), "[.ShellClassInfo]");

        Assert.Empty(provider.Scan(new ScanContext { UserRoot = "HKCU" }));
    }
}
