using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class RegistryRunProviderTests
{
    private const string HklmRun = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string HkcuRun = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    private const string HkcuApproved = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string HklmApproved32 = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    private static (RegistryRunProvider Provider, FakeRegistry Registry, FakeFileSystem Fs) Sut()
    {
        var registry = new FakeRegistry();
        var fs = new FakeFileSystem();
        return (new RegistryRunProvider(registry, fs), registry, fs);
    }

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _, _) = Sut();
        Assert.Equal("registry.run", provider.Id);
        Assert.Equal(StartupCategory.RunKey, provider.Category);
    }

    [Fact]
    public void TableCoversTheKeyFamiliesFromTheSpec()
    {
        Assert.Contains(RunKeyTable.All, d => d.SubKey.EndsWith(@"CurrentVersion\Run") && d.RootToken == "HKLM");
        Assert.Contains(RunKeyTable.All, d => d.SubKey.EndsWith("RunOnce"));
        Assert.Contains(RunKeyTable.All, d => d.SubKey.EndsWith("RunOnceEx") && d.WalkSubKeys);
        Assert.Contains(RunKeyTable.All, d => d.SubKey.EndsWith("RunServices"));
        Assert.Contains(RunKeyTable.All, d => d.SubKey.EndsWith("RunServicesOnce"));
        Assert.Contains(RunKeyTable.All, d => d.SubKey.EndsWith(@"Policies\Explorer\Run"));
        Assert.Contains(RunKeyTable.All, d => d.RootToken == "%USER%");
        Assert.Contains(RunKeyTable.All, d => d.View == RegistryViewKind.Registry32);
        // Exact match: the Terminal Server Install row also ends with "CurrentVersion\Run".
        Assert.Equal("Run32", RunKeyTable.All.Single(d =>
            d.RootToken == "HKLM" && d.View == RegistryViewKind.Registry32 &&
            d.SubKey == @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run").ApprovedSubKey);
    }

    [Fact]
    public void ScansAnHklmRunValueIntoAnItem()
    {
        var (provider, registry, _) = Sut();
        registry.Set(HklmRun, "OneDrive", @"""C:\OneDrive.exe"" /background",
                     RegistryValueType.String, RegistryViewKind.Registry64);

        var item = Assert.Single(provider.Scan(Ctx()).Where(i => i.Name == "OneDrive"));

        Assert.Equal("registry.run", item.ProviderId);
        Assert.Equal(HklmRun, item.Location);
        Assert.Equal(@"""C:\OneDrive.exe"" /background", item.RawValue);
        Assert.Equal(@"C:\OneDrive.exe", item.ImagePath);
        Assert.Equal("/background", item.Arguments);
        Assert.Equal(StartupCategory.RunKey, item.Category);
        Assert.Null(item.UserSid);
    }

    [Fact]
    public void PerUserRowsFollowUserRootAndCarryTheSid()
    {
        var (provider, registry, _) = Sut();
        registry.Set(@"HKU\S-1-5-21-7\Software\Microsoft\Windows\CurrentVersion\Run", "Spotify",
                     @"C:\Spotify.exe", RegistryValueType.String);

        var ctx = new ScanContext { UserRoot = @"HKU\S-1-5-21-7", UserSid = "S-1-5-21-7", ScanningMountedHive = true };
        var item = Assert.Single(provider.Scan(ctx).Where(i => i.Name == "Spotify"));

        Assert.Equal("S-1-5-21-7", item.UserSid);
        Assert.StartsWith(@"HKU\S-1-5-21-7\", item.Location);
    }

    [Fact]
    public void IdIsUnchangedWhenOnlyTheCommandLineChanges()
    {
        var (provider, registry, _) = Sut();
        registry.Set(HkcuRun, "Thing", "old.exe", RegistryValueType.String);
        var before = Assert.Single(provider.Scan(Ctx()).Where(i => i.Name == "Thing")).Id;

        registry.Set(HkcuRun, "Thing", "new.exe", RegistryValueType.String);
        var after = Assert.Single(provider.Scan(Ctx()).Where(i => i.Name == "Thing")).Id;

        Assert.Equal(before, after);
    }

    [Fact]
    public void EntryWithNoStartupApprovedBlobReadsAsEnabled()
    {
        var (provider, registry, _) = Sut();
        registry.Set(HkcuRun, "Thing", "x.exe", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()).Where(i => i.Name == "Thing"));

        Assert.Equal(EnableState.Enabled, item.State);
        Assert.Contains("Default", item.StateSource);
    }

    [Fact]
    public void DisabledStartupApprovedBlobMakesTheItemDisabled()
    {
        var (provider, registry, _) = Sut();
        registry.Set(HkcuRun, "Thing", "x.exe", RegistryValueType.String);
        registry.Set(HkcuApproved, "Thing",
            StartupApprovedBlob.Encode(false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc), null),
            RegistryValueType.Binary);

        var item = Assert.Single(provider.Scan(Ctx()).Where(i => i.Name == "Thing"));

        Assert.Equal(EnableState.Disabled, item.State);
        Assert.Equal("StartupApproved", item.StateSource);
    }

    [Fact]
    public void ThirtyTwoBitHklmRunUsesTheRun32ApprovedKey()
    {
        var (provider, registry, _) = Sut();
        registry.Set(HklmRun, "Legacy", "x.exe", RegistryValueType.String, RegistryViewKind.Registry32);
        registry.Set(HklmApproved32, "Legacy",
            StartupApprovedBlob.Encode(false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc), null),
            RegistryValueType.Binary, RegistryViewKind.Registry32);

        var item = Assert.Single(provider.Scan(Ctx()).Where(i => i.Name == "Legacy"));

        Assert.Equal(EnableState.Disabled, item.State);
    }

    [Fact]
    public void RunOnceHasNoStartupApprovedCoverageSoItReadsAsEnabled()
    {
        var (provider, registry, _) = Sut();
        registry.Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce", "Once", "x.exe", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()).Where(i => i.Name == "Once"));

        Assert.Equal(EnableState.Enabled, item.State);
        Assert.Equal("NoStartupApprovedCoverage", item.StateSource);
    }

    [Fact]
    public void RunOnceExCommandsOneLevelDeepAreFound()
    {
        var (provider, registry, _) = Sut();
        registry.Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnceEx\0001", "0000",
                     @"C:\setup.exe", RegistryValueType.String, RegistryViewKind.Registry64);

        var item = Assert.Single(provider.Scan(Ctx()).Where(i => i.Name == "0000"));

        Assert.EndsWith(@"RunOnceEx\0001", item.Location);
    }

    [Fact]
    public void AccessDeniedKeyBecomesAScanErrorAndDoesNotStopTheOtherKeys()
    {
        var (provider, registry, _) = Sut();
        registry.Set(HklmRun, "Blocked", "x.exe", RegistryValueType.String, RegistryViewKind.Registry64);
        registry.Deny(HklmRun, RegistryViewKind.Registry64);
        registry.Set(HkcuRun, "Readable", "y.exe", RegistryValueType.String);
        var ctx = Ctx();

        var names = provider.Scan(ctx).Select(i => i.Name).ToList();

        Assert.Contains("Readable", names);
        Assert.DoesNotContain("Blocked", names);
        Assert.Contains(ctx.Errors, e => e.ProviderId == "registry.run" && e.Location == HklmRun);
    }

    [Fact]
    public void NonStringValuesAreRenderedForDisplayRatherThanSkipped()
    {
        var (provider, registry, _) = Sut();
        registry.Set(HkcuRun, "Weird", new byte[] { 0x01, 0x02 }, RegistryValueType.Binary);

        var item = Assert.Single(provider.Scan(Ctx()).Where(i => i.Name == "Weird"));

        Assert.Equal("01 02", item.RawValue);
    }
}
