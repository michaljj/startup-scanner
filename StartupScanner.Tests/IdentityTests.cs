using StartupScanner.Core.Models;

namespace StartupScanner.Tests;

public class IdentityTests
{
    [Fact]
    public void IdIsStableForTheSameInputs()
    {
        var a = Identity.Compute("registry.run", null, @"HKLM\SOFTWARE\Run", "OneDrive");
        var b = Identity.Compute("registry.run", null, @"HKLM\SOFTWARE\Run", "OneDrive");
        Assert.Equal(a, b);
    }

    [Fact]
    public void IdIsLowercaseHexSha256()
    {
        var id = Identity.Compute("registry.run", null, @"HKLM\SOFTWARE\Run", "OneDrive");
        Assert.Equal(64, id.Length);
        Assert.Matches("^[0-9a-f]{64}$", id);
    }

    [Fact]
    public void IdIgnoresRawValueSoAChangedCommandLineIsNotANewItem()
    {
        var item1 = NewItem(@"C:\a\one.exe");
        var item2 = NewItem(@"C:\b\two.exe --flag");
        Assert.Equal(item1.Id, item2.Id);
    }

    [Fact]
    public void IdVariesWithUserSid()
    {
        var a = Identity.Compute("registry.run", "S-1-5-21-1", @"HKCU\Run", "X");
        var b = Identity.Compute("registry.run", "S-1-5-21-2", @"HKCU\Run", "X");
        Assert.NotEqual(a, b);
    }

    private static StartupItem NewItem(string raw) => new()
    {
        Id = Identity.Compute("registry.run", null, @"HKLM\SOFTWARE\Run", "Thing"),
        ProviderId = "registry.run",
        Location = @"HKLM\SOFTWARE\Run",
        Name = "Thing",
        RawValue = raw,
        Category = StartupCategory.RunKey,
    };
}
