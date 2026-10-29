using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Scanning;

namespace StartupScanner.Core.Providers;

public sealed class BootExecuteProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    public const string SessionManagerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager";

    private static readonly IReadOnlyList<ValueSource> Sources = new[]
    {
        new ValueSource("HKLM", SessionManagerKey, RegistryViewKind.Default,
            new[] { "BootExecute", "SetupExecute", "PlatformExecute", "S0InitialCommand" }),
    };

    private readonly TableValueScanner _values = new(registry, fs);

    public string Id => "bootexecute";
    public StartupCategory Category => StartupCategory.BootExecute;

    public IEnumerable<StartupItem> Scan(ScanContext ctx) => _values.Scan(Id, Category, Sources, ctx);
}
