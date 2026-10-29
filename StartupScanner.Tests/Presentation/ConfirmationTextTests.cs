using StartupScanner.Core.Models;
using StartupScanner.Core.Presentation;

namespace StartupScanner.Tests.Presentation;

public class ConfirmationTextTests
{
    private static StartupItem Item(RiskTier risk, StartupCategory category, string name = "Thing") => new()
    {
        Id = "id", ProviderId = "p", Location = "loc", Name = name, RawValue = "r",
        Category = category, Risk = risk,
    };

    [Fact]
    public void MediumRiskGetsAPlainConfirmation()
    {
        var text = ConfirmationText.For(Item(RiskTier.Medium, StartupCategory.Service, "Spooler"), enabling: false);

        Assert.Contains("Disable", text);
        Assert.Contains("Spooler", text);
    }

    [Fact]
    public void EnablingSaysEnable()
    {
        Assert.Contains("Enable", ConfirmationText.For(Item(RiskTier.Medium, StartupCategory.Service), enabling: true));
    }

    [Theory]
    [InlineData(StartupCategory.Winlogon, "logging in")]
    [InlineData(StartupCategory.BootExecute, "booting")]
    [InlineData(StartupCategory.WinsockLsp, "network")]
    [InlineData(StartupCategory.AppInit, "application")]
    [InlineData(StartupCategory.Ifeo, "redirects")]
    public void HighRiskNamesTheConcreteConsequence(StartupCategory category, string expectedFragment)
    {
        var text = ConfirmationText.For(Item(RiskTier.High, category), enabling: false);

        Assert.Contains(expectedFragment, text, StringComparison.OrdinalIgnoreCase);
    }
}
