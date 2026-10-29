using System.Text;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class WinsockLspProviderTests
{
    private const string Protocol =
        @"HKLM\SYSTEM\CurrentControlSet\Services\WinSock2\Parameters\Protocol_Catalog9\Catalog_Entries";
    private const string NameSpace =
        @"HKLM\SYSTEM\CurrentControlSet\Services\WinSock2\Parameters\NameSpace_Catalog5\Catalog_Entries";

    private static (WinsockLspProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new WinsockLspProvider(registry, new FakeFileSystem()), registry);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    /// <summary>A PackedCatalogItem starts with a fixed 256-WCHAR, null-terminated path buffer.</summary>
    private static byte[] PackedCatalogItem(string path)
    {
        var blob = new byte[512 + 64];
        Encoding.Unicode.GetBytes(path).CopyTo(blob, 0);
        return blob;
    }

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("winsock-lsp", provider.Id);
        Assert.Equal(StartupCategory.WinsockLsp, provider.Category);
    }

    [Fact]
    public void ParsesTheDllPathOutOfAPackedCatalogItem()
    {
        Assert.Equal(@"C:\Windows\System32\mswsock.dll",
            WinsockLspProvider.ParsePackedCatalogItem(PackedCatalogItem(@"C:\Windows\System32\mswsock.dll")));
    }

    [Fact]
    public void ATruncatedBlobParsesAsNullRatherThanThrowing()
    {
        Assert.Null(WinsockLspProvider.ParsePackedCatalogItem(new byte[] { 0x43, 0x00, 0x3A }));
    }

    [Fact]
    public void AnAllZeroBlobParsesAsNull()
    {
        Assert.Null(WinsockLspProvider.ParsePackedCatalogItem(new byte[600]));
    }

    [Fact]
    public void ReportsAProtocolCatalogEntry()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Protocol}\000000000001", "PackedCatalogItem",
                     PackedCatalogItem(@"C:\evil\lsp.dll"), RegistryValueType.Binary);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal("000000000001", item.Name);
        Assert.Equal($@"{Protocol}\000000000001", item.Location);
        Assert.Equal(@"C:\evil\lsp.dll", item.RawValue);
        Assert.Equal(@"C:\evil\lsp.dll", item.ImagePath);
        Assert.Equal("PackedCatalogItem", item.Extra["ValueName"]);
        Assert.Equal("Protocol_Catalog9", item.Extra["Catalog"]);
        Assert.Equal(RiskTier.High, RiskTiers.For(item));
    }

    [Fact]
    public void ReportsANameSpaceCatalogEntryFromItsLibraryPath()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{NameSpace}\000000000002", "LibraryPath", @"%SystemRoot%\System32\nlaapi.dll",
                     RegistryValueType.ExpandString);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal(@"%SystemRoot%\System32\nlaapi.dll", item.RawValue);
        Assert.Equal("LibraryPath", item.Extra["ValueName"]);
        Assert.Equal("NameSpace_Catalog5", item.Extra["Catalog"]);
    }

    [Fact]
    public void AnUnparseableEntryIsReportedAndMarkedPartiallyRead()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Protocol}\000000000003", "PackedCatalogItem", new byte[4], RegistryValueType.Binary);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.True(item.PartiallyRead);
        Assert.Null(item.ImagePath);
    }

    [Fact]
    public void EntriesWithNeitherValueAreIgnored()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Protocol}\000000000004", "Other", 1, RegistryValueType.Dword);

        Assert.Empty(provider.Scan(Ctx()));
    }
}
