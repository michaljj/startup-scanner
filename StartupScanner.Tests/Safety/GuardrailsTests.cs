using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;

namespace StartupScanner.Tests.Safety;

public class GuardrailsTests
{
    private static StartupItem Item(StartupCategory category, string name, string raw,
                                    IDictionary<string, string>? extra = null) => new()
    {
        Id = "id", ProviderId = "p", Location = "loc", Name = name, RawValue = raw, Category = category,
        Extra = new Dictionary<string, string>(extra ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
    };

    [Theory]
    [InlineData(@"%systemroot%\system32\userinit.exe,")]
    [InlineData(@"%SystemRoot%\System32\userinit.exe")]
    [InlineData(@"C:\Windows\system32\userinit.exe,")]
    public void DefaultUserinitIsBlocked(string raw)
    {
        var decision = Guardrails.Evaluate(Item(StartupCategory.Winlogon, "Userinit", raw));

        Assert.True(decision.Blocked);
        Assert.Contains("logon", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModifiedUserinitIsNotBlocked()
    {
        var decision = Guardrails.Evaluate(
            Item(StartupCategory.Winlogon, "Userinit", @"C:\Windows\system32\userinit.exe,C:\evil.exe"));

        Assert.False(decision.Blocked);
    }

    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("Explorer.exe")]
    public void DefaultShellIsBlocked(string raw)
    {
        Assert.True(Guardrails.Evaluate(Item(StartupCategory.Winlogon, "Shell", raw)).Blocked);
    }

    [Fact]
    public void ModifiedShellIsNotBlocked()
    {
        Assert.False(Guardrails.Evaluate(
            Item(StartupCategory.Winlogon, "Shell", "explorer.exe,evil.exe")).Blocked);
    }

    [Fact]
    public void DefaultBootExecuteIsBlocked()
    {
        var decision = Guardrails.Evaluate(
            Item(StartupCategory.BootExecute, "BootExecute", "autocheck autochk *"));

        Assert.True(decision.Blocked);
        Assert.Contains("autochk", decision.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtraBootExecuteEntriesAreNotBlocked()
    {
        Assert.False(Guardrails.Evaluate(
            Item(StartupCategory.BootExecute, "BootExecute", "autocheck autochk *; evil.exe")).Blocked);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void BootAndSystemStartDriversAreBlocked(int start)
    {
        var decision = Guardrails.Evaluate(Item(StartupCategory.Driver, "disk", "driver.sys",
            new Dictionary<string, string> { ["Start"] = start.ToString() }));

        Assert.True(decision.Blocked);
        Assert.Contains("boot", decision.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void DemandAndAutoStartServicesAreNotBlocked(int start)
    {
        Assert.False(Guardrails.Evaluate(Item(StartupCategory.Service, "Spooler", "svc.exe",
            new Dictionary<string, string> { ["Start"] = start.ToString() })).Blocked);
    }

    [Theory]
    [InlineData("DcomLaunch")]
    [InlineData("RpcSs")]
    [InlineData("RpcEptMapper")]
    [InlineData("PlugPlay")]
    [InlineData("Power")]
    [InlineData("ProfSvc")]
    [InlineData("LSM")]
    [InlineData("BrokerInfrastructure")]
    [InlineData("SamSs")]
    [InlineData("EventLog")]
    [InlineData("rpcss")]
    public void NamedCriticalServicesAreBlockedRegardlessOfStartValue(string name)
    {
        var decision = Guardrails.Evaluate(Item(StartupCategory.Service, name, "svc.exe",
            new Dictionary<string, string> { ["Start"] = "3" }));

        Assert.True(decision.Blocked);
    }

    [Fact]
    public void EveryLsaPackageEntryIsBlockedAsACategory()
    {
        var decision = Guardrails.Evaluate(
            Item(StartupCategory.Lsa, "Authentication Packages", "msv1_0"));

        Assert.True(decision.Blocked);
        Assert.Contains("logon", decision.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OrdinaryRunKeysAreNotBlocked()
    {
        Assert.False(Guardrails.Evaluate(
            Item(StartupCategory.RunKey, "Spotify", @"C:\Spotify.exe")).Blocked);
    }

    [Fact]
    public void TheNamedCriticalServiceListMatchesTheSpec()
    {
        Assert.Equal(
            new[] { "BrokerInfrastructure", "DcomLaunch", "EventLog", "LSM", "PlugPlay", "Power",
                    "ProfSvc", "RpcEptMapper", "RpcSs", "SamSs" },
            Guardrails.CriticalServices.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }
}
