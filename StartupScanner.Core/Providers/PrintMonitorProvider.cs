using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Providers;

/// <summary>
/// Print monitors and print providers load a DLL named by their Driver value. Disabling targets
/// that value rather than the whole monitor subkey: a value-level change restores exactly from the
/// journal and leaves the rest of the monitor's configuration untouched.
/// </summary>
public sealed class PrintMonitorProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    private static readonly string[] Roots =
    {
        @"HKLM\SYSTEM\CurrentControlSet\Control\Print\Monitors",
        @"HKLM\SYSTEM\CurrentControlSet\Control\Print\Providers",
    };

    public string Id => "print-monitor";
    public StartupCategory Category => StartupCategory.PrintMonitor;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        foreach (var root in Roots)
            foreach (var item in ScanRoot(root, ctx))
                yield return item;
    }

    private IReadOnlyList<StartupItem> ScanRoot(string rootPath, ScanContext ctx)
    {
        var found = new List<StartupItem>();
        IReadOnlyList<string> names;

        try
        {
            using var root = registry.OpenKey(rootPath);
            if (root is null) return found;
            names = root.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, rootPath, ex.Message));
            return found;
        }

        foreach (var name in names)
        {
            var path = $@"{rootPath}\{name}";
            try
            {
                using var key = registry.OpenKey(path);
                var driver = key is null
                    ? ""
                    : RegistryValueCodec.ToDisplay(key.GetValue("Driver"), key.GetValueType("Driver"));
                if (string.IsNullOrWhiteSpace(driver)) continue;

                var (image, args) = CommandLine.Split(driver, fs);

                found.Add(new StartupItem
                {
                    Id = Identity.Compute(Id, null, path, name),
                    ProviderId = Id,
                    Location = path,
                    Name = name,
                    RawValue = driver,
                    Category = Category,
                    ImagePath = image,
                    Arguments = args,
                    State = EnableState.Enabled,
                    StateSource = "ValuePresent",
                    Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ValueName"] = "Driver",
                    },
                });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                ctx.Errors.Add(new ScanError(Id, path, ex.Message));
            }
        }

        return found;
    }
}
