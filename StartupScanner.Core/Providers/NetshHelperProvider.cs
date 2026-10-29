using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Scanning;

namespace StartupScanner.Core.Providers;

public sealed class NetshHelperProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    private static readonly IReadOnlyList<ValueSource> Sources = new[]
    {
        new ValueSource("HKLM", @"SOFTWARE\Microsoft\Netsh", RegistryViewKind.Default, null),
    };

    private readonly TableValueScanner _values = new(registry, fs);

    public string Id => "netsh-helper";
    public StartupCategory Category => StartupCategory.NetshHelper;

    public IEnumerable<StartupItem> Scan(ScanContext ctx) => _values.Scan(Id, Category, Sources, ctx);
}
