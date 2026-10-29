using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;

namespace StartupScanner.Tests.Safety;

public class RiskTiersTests
{
    private static StartupItem Item(StartupCategory category, string name = "n", string raw = "r",
                                    string location = "loc", IDictionary<string, string>? extra = null) => new()
    {
        Id = "id", ProviderId = "p", Location = location, Name = name, RawValue = raw, Category = category,
        Extra = new Dictionary<string, string>(extra ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
    };

    [Theory]
    [InlineData(StartupCategory.RunKey)]
    [InlineData(StartupCategory.StartupFolder)]
    [InlineData(StartupCategory.AppxStartupTask)]
    public void RunKeysFoldersAndAppxAreLow(StartupCategory category)
    {
        Assert.Equal(RiskTier.Low, RiskTiers.For(Item(category)));
    }

    [Fact]
    public void ServicesAreMedium()
    {
        Assert.Equal(RiskTier.Medium, RiskTiers.For(Item(StartupCategory.Service, "Spooler",
            extra: new Dictionary<string, string> { ["Start"] = "2" })));
    }

    [Fact]
    public void TasksUnderMicrosoftWindowsAreMediumAndOtherTasksAreLow()
    {
        Assert.Equal(RiskTier.Medium, RiskTiers.For(Item(StartupCategory.ScheduledTask, location: @"\Microsoft\Windows\Defrag")));
        Assert.Equal(RiskTier.Low, RiskTiers.For(Item(StartupCategory.ScheduledTask, location: @"\Vendor")));
    }

    [Theory]
    [InlineData(StartupCategory.ActiveSetup)]
    [InlineData(StartupCategory.GpoScript)]
    public void ActiveSetupAndGpoScriptsAreMedium(StartupCategory category)
    {
        Assert.Equal(RiskTier.Medium, RiskTiers.For(Item(category)));
    }

    [Theory]
    [InlineData(StartupCategory.Winlogon, "Taskman")]
    [InlineData(StartupCategory.BootExecute, "SetupExecute")]
    [InlineData(StartupCategory.WinsockLsp, "LibraryPath")]
    public void WinlogonBootExecuteAndWinsockAreHigh(StartupCategory category, string name)
    {
        Assert.Equal(RiskTier.High, RiskTiers.For(Item(category, name)));
    }

    [Fact]
    public void IfeoDebuggerIsHighAndOtherIfeoValuesAreMedium()
    {
        Assert.Equal(RiskTier.High, RiskTiers.For(Item(StartupCategory.Ifeo, "Debugger")));
        Assert.Equal(RiskTier.Medium, RiskTiers.For(Item(StartupCategory.Ifeo, "GlobalFlag")));
    }

    [Fact]
    public void AppInitDllsIsHighAndAppCertDllsIsMedium()
    {
        Assert.Equal(RiskTier.High, RiskTiers.For(Item(StartupCategory.AppInit, "AppInit_DLLs")));
        Assert.Equal(RiskTier.Medium, RiskTiers.For(Item(StartupCategory.AppInit, "AppCertDlls")));
    }

    [Fact]
    public void AnythingTheBlockedListCatchesIsCritical()
    {
        Assert.Equal(RiskTier.Critical, RiskTiers.For(Item(StartupCategory.Winlogon, "Shell", "explorer.exe")));
        Assert.Equal(RiskTier.Critical, RiskTiers.For(Item(StartupCategory.Lsa, "Security Packages")));
        Assert.Equal(RiskTier.Critical, RiskTiers.For(Item(StartupCategory.Service, "RpcSs",
            extra: new Dictionary<string, string> { ["Start"] = "2" })));
    }
}
