using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Scanning;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Providers;

public sealed class ExplorerHooksProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    private const string UserWindowsKey = @"Software\Microsoft\Windows NT\CurrentVersion\Windows";
    private const string ShellExecuteHooks = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellExecuteHooks";
    private const string SharedTaskScheduler = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SharedTaskScheduler";
    private const string DelayLoad = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\ShellServiceObjectDelayLoad";

    private static readonly IReadOnlyList<ValueSource> PlainSources = new[]
    {
        new ValueSource("%USER%", UserWindowsKey, RegistryViewKind.Default, new[] { "Load", "Run" }),
    };

    /// <summary>
    /// Where the CLSID lives differs by key: the hook lists use the value name, while
    /// ShellServiceObjectDelayLoad puts a description in the name and the CLSID in the data.
    /// </summary>
    private static readonly (string Path, bool ClsidIsValueName)[] ClsidSources =
    {
        (ShellExecuteHooks, true),
        (SharedTaskScheduler, true),
        (DelayLoad, false),
    };

    private readonly TableValueScanner _values = new(registry, fs);
    private readonly ClsidResolver _clsids = new(registry);

    public string Id => "explorer-hooks";
    public StartupCategory Category => StartupCategory.ExplorerHook;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        foreach (var item in _values.Scan(Id, Category, PlainSources, ctx)) yield return item;

        foreach (var (path, clsidIsValueName) in ClsidSources)
            foreach (var item in ScanClsidList(path, clsidIsValueName, ctx))
                yield return item;
    }

    private IReadOnlyList<StartupItem> ScanClsidList(string path, bool clsidIsValueName, ScanContext ctx)
    {
        var found = new List<StartupItem>();

        try
        {
            using var key = registry.OpenKey(path);
            if (key is null) return found;

            foreach (var name in key.GetValueNames())
            {
                var data = RegistryValueCodec.ToDisplay(key.GetValue(name), key.GetValueType(name));
                var clsid = clsidIsValueName ? name : data;
                if (string.IsNullOrWhiteSpace(clsid)) continue;

                var dll = _clsids.ResolveInprocServer(clsid, ctx.UserRoot);

                found.Add(new StartupItem
                {
                    Id = Identity.Compute(Id, null, path, name),
                    ProviderId = Id,
                    Location = path,
                    Name = name,
                    RawValue = data,
                    Category = Category,
                    ImagePath = dll,
                    State = EnableState.Enabled,
                    StateSource = "ValuePresent",
                    PartiallyRead = dll is null,
                    Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Clsid"] = clsid,
                        ["ValueName"] = name,
                    },
                });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, path, ex.Message));
        }

        return found;
    }
}
