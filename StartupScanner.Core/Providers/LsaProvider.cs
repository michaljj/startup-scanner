using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Scanning;

namespace StartupScanner.Core.Providers;

public sealed class LsaProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    private const string LsaKey = @"SYSTEM\CurrentControlSet\Control\Lsa";

    private static readonly string[] PackageValues =
        { "Authentication Packages", "Notification Packages", "Security Packages" };

    private static readonly IReadOnlyList<ValueSource> Sources = new[]
    {
        new ValueSource("HKLM", LsaKey, RegistryViewKind.Default, PackageValues),
        new ValueSource("HKLM", LsaKey + @"\OSConfig", RegistryViewKind.Default, PackageValues),
    };

    private readonly TableValueScanner _values = new(registry, fs);

    public string Id => "lsa";
    public StartupCategory Category => StartupCategory.Lsa;

    public IEnumerable<StartupItem> Scan(ScanContext ctx) => _values.Scan(Id, Category, Sources, ctx);
}
