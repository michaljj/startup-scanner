using StartupScanner.Core.Abstractions;

namespace StartupScanner.Tests.Fakes;

public class FakeRegistryTests
{
    [Fact]
    public void ReadsBackAValueItWasSeededWith()
    {
        var reg = new FakeRegistry();
        reg.Set(@"HKLM\SOFTWARE\X", "Name", "value", RegistryValueType.String);

        using var key = reg.OpenKey(@"HKLM\SOFTWARE\X");

        Assert.NotNull(key);
        Assert.Equal(new[] { "Name" }, key!.GetValueNames());
        Assert.Equal("value", key.GetValue("Name"));
        Assert.Equal(RegistryValueType.String, key.GetValueType("Name"));
    }

    [Fact]
    public void MissingKeyOpensAsNull()
    {
        Assert.Null(new FakeRegistry().OpenKey(@"HKLM\SOFTWARE\Nope"));
    }

    [Fact]
    public void ViewsAreIsolated()
    {
        var reg = new FakeRegistry();
        reg.Set(@"HKLM\SOFTWARE\X", "N", "64", RegistryValueType.String, RegistryViewKind.Registry64);
        reg.Set(@"HKLM\SOFTWARE\X", "N", "32", RegistryValueType.String, RegistryViewKind.Registry32);

        Assert.Equal("64", reg.OpenKey(@"HKLM\SOFTWARE\X", RegistryViewKind.Registry64)!.GetValue("N"));
        Assert.Equal("32", reg.OpenKey(@"HKLM\SOFTWARE\X", RegistryViewKind.Registry32)!.GetValue("N"));
    }

    [Fact]
    public void EnumeratesImmediateSubKeyNamesOnly()
    {
        var reg = new FakeRegistry();
        reg.Set(@"HKLM\A\B", "x", 1, RegistryValueType.Dword);
        reg.Set(@"HKLM\A\C\D", "x", 1, RegistryValueType.Dword);

        using var a = reg.OpenKey(@"HKLM\A");

        Assert.Equal(new[] { "B", "C" }, a!.GetSubKeyNames().OrderBy(n => n).ToArray());
    }

    [Fact]
    public void DeniedKeyThrowsUnauthorizedAccess()
    {
        var reg = new FakeRegistry();
        reg.Set(@"HKLM\Secret", "x", 1, RegistryValueType.Dword);
        reg.Deny(@"HKLM\Secret");

        Assert.Throws<UnauthorizedAccessException>(() => reg.OpenKey(@"HKLM\Secret"));
    }

    [Fact]
    public void DeleteValueRemovesIt()
    {
        var reg = new FakeRegistry();
        reg.Set(@"HKLM\A", "x", 1, RegistryValueType.Dword);

        using var key = reg.OpenKey(@"HKLM\A", writable: true);
        key!.DeleteValue("x");

        Assert.Empty(reg.OpenKey(@"HKLM\A")!.GetValueNames());
    }

    [Fact]
    public void CreateKeyMakesAWritableKey()
    {
        var reg = new FakeRegistry();

        using var key = reg.CreateKey(@"HKCU\New\Path");
        key.SetValue("v", 3, RegistryValueType.Dword);

        Assert.Equal(3, reg.OpenKey(@"HKCU\New\Path")!.GetValue("v"));
    }
}
