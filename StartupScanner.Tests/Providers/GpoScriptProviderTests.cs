using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class GpoScriptProviderTests
{
    private const string MachineIni = @"C:\Windows\System32\GroupPolicy\Machine\Scripts\scripts.ini";
    private const string UserIni = @"C:\Windows\System32\GroupPolicy\User\Scripts\scripts.ini";

    private const string SampleIni = """
        [Startup]
        0CmdLine=logon.bat
        0Parameters=/quiet
        1CmdLine=\\server\share\second.cmd
        1Parameters=
        [Shutdown]
        0CmdLine=bye.bat
        0Parameters=
        """;

    private static (GpoScriptProvider Provider, FakeFileSystem Fs) Sut()
    {
        var fs = new FakeFileSystem();
        fs.SetEnvironment("SystemRoot", @"C:\Windows");
        return (new GpoScriptProvider(fs, new FakeRegistry()), fs);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("gpo-script", provider.Id);
        Assert.Equal(StartupCategory.GpoScript, provider.Category);
    }

    [Fact]
    public void ParsesSectionsAndIndexedPairs()
    {
        var entries = GpoScriptProvider.ParseScriptsIni(SampleIni);

        Assert.Equal(3, entries.Count);
        Assert.Equal(("Startup", 0, "logon.bat", "/quiet"),
            (entries[0].Section, entries[0].Index, entries[0].CmdLine, entries[0].Parameters));
        Assert.Equal(("Startup", 1, @"\\server\share\second.cmd", ""),
            (entries[1].Section, entries[1].Index, entries[1].CmdLine, entries[1].Parameters));
        Assert.Equal("Shutdown", entries[2].Section);
    }

    [Fact]
    public void IgnoresBlankLinesAndUnknownKeys()
    {
        var entries = GpoScriptProvider.ParseScriptsIni("[Logon]\n\nnonsense\n0CmdLine=a.bat\n");

        var entry = Assert.Single(entries);
        Assert.Equal("a.bat", entry.CmdLine);
        Assert.Equal("", entry.Parameters);
    }

    [Fact]
    public void ReportsMachineStartupScripts()
    {
        var (provider, fs) = Sut();
        fs.AddFile(MachineIni, SampleIni);

        var items = provider.Scan(Ctx()).ToList();

        var startup = items.Single(i => i.Name == "Startup[0]");
        Assert.Equal(MachineIni, startup.Location);
        Assert.Equal("logon.bat /quiet", startup.RawValue);
        Assert.Equal("Startup", startup.Extra["Section"]);
        Assert.Equal("0", startup.Extra["Index"]);
        Assert.Equal(EnableState.Enabled, startup.State);
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void ReportsUserScriptsWithTheUserSid()
    {
        var (provider, fs) = Sut();
        fs.AddFile(UserIni, "[Logon]\n0CmdLine=user.bat\n0Parameters=\n");

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU", UserSid = "S-1-5-21-9" }));

        Assert.Equal(UserIni, item.Location);
        Assert.Equal("S-1-5-21-9", item.UserSid);
    }

    [Fact]
    public void RecordsTheRegistryStateCopyWhenPresent()
    {
        var fs = new FakeFileSystem();
        fs.SetEnvironment("SystemRoot", @"C:\Windows");
        fs.AddFile(MachineIni, "[Startup]\n0CmdLine=logon.bat\n0Parameters=\n");
        var registry = new FakeRegistry();
        registry.Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\State\Machine\Scripts\Startup\0\0",
                     "Script", "logon.bat", Core.Abstractions.RegistryValueType.String);

        var item = Assert.Single(new GpoScriptProvider(fs, registry).Scan(Ctx()));

        Assert.Equal("logon.bat", item.Extra["RegistryStateScript"]);
    }

    [Fact]
    public void NoIniFilesYieldsNothing()
    {
        var (provider, _) = Sut();
        Assert.Empty(provider.Scan(Ctx()));
    }

    [Fact]
    public void DisableJournalsTheWholeIniAndRewritesItWithoutTheEntry()
    {
        var (provider, fs) = Sut();
        fs.AddFile(MachineIni, SampleIni);
        var item = provider.Scan(Ctx()).Single(i => i.Name == "Startup[0]");
        var toggle = new GpoScriptToggle(fs, new FakeClock());

        var record = toggle.SetEnabled(item, enabled: false, new FakeJournal());

        Assert.Equal("GpoScript.Ini", record.Mechanism);
        Assert.Equal(SampleIni, record.Before);
        var rewritten = fs.ReadAllText(MachineIni);
        Assert.DoesNotContain("0CmdLine=logon.bat", rewritten);
        Assert.Contains("1CmdLine=", rewritten);
        Assert.Contains("[Shutdown]", rewritten);
    }

    [Fact]
    public void EnableRestoresTheIniByteForByte()
    {
        var (provider, fs) = Sut();
        fs.AddFile(MachineIni, SampleIni);
        var item = provider.Scan(Ctx()).Single(i => i.Name == "Startup[0]");
        var toggle = new GpoScriptToggle(fs, new FakeClock());
        var journal = new FakeJournal();
        journal.Record(toggle.SetEnabled(item, enabled: false, journal));

        toggle.SetEnabled(item with { State = EnableState.Disabled }, enabled: true, journal);

        Assert.Equal(SampleIni, fs.ReadAllText(MachineIni));
    }
}
