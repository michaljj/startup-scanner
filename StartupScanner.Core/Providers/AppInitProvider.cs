using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Scanning;

namespace StartupScanner.Core.Providers;

public sealed class AppInitProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    private const string WindowsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows";
    private const string AppCertKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\AppCertDlls";

    private static readonly IReadOnlyList<ValueSource> Sources = new[]
    {
        // AppInit_DLLs is per-view: 32-bit processes read the WOW6432Node copy.
        new ValueSource("HKLM", WindowsKey, RegistryViewKind.Registry64,
            new[] { "AppInit_DLLs", "LoadAppInit_DLLs" }),
        new ValueSource("HKLM", WindowsKey, RegistryViewKind.Registry32,
            new[] { "AppInit_DLLs", "LoadAppInit_DLLs" }),
        new ValueSource("HKLM", AppCertKey, RegistryViewKind.Default, null),
    };

    private readonly TableValueScanner _values = new(registry, fs);

    public string Id => "appinit";
    public StartupCategory Category => StartupCategory.AppInit;

    public IEnumerable<StartupItem> Scan(ScanContext ctx) => _values.Scan(Id, Category, Sources, ctx);
}
