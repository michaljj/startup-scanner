using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Scanning;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Providers;

public sealed class WinlogonProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    public const string MachineKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    public const string UserKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    public const string ProviderIdValue = "winlogon";

    private static readonly IReadOnlyList<ValueSource> Sources = new[]
    {
        new ValueSource("HKLM", MachineKey, RegistryViewKind.Default,
            new[] { "Userinit", "Shell", "Taskman", "AppSetup", "GinaDLL" }),
        new ValueSource("%USER%", UserKey, RegistryViewKind.Default,
            new[] { "Shell", "Taskman" }),
    };

    private readonly TableValueScanner _values = new(registry, fs);

    public string Id => ProviderIdValue;
    public StartupCategory Category => StartupCategory.Winlogon;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        foreach (var item in _values.Scan(Id, Category, Sources, ctx)) yield return item;
        foreach (var item in ScanNotify(ctx)) yield return item;
    }

    /// <summary>Winlogon\Notify packages are subkeys carrying a DLLName rather than flat values.</summary>
    private IReadOnlyList<StartupItem> ScanNotify(ScanContext ctx)
    {
        var notifyPath = $@"HKLM\{MachineKey}\Notify";
        var found = new List<StartupItem>();

        try
        {
            using var root = registry.OpenKey(notifyPath);
            if (root is null) return found;

            foreach (var name in root.GetSubKeyNames())
            {
                using var child = root.OpenSubKey(name);
                var raw = RegistryValueCodec.ToDisplay(child?.GetValue("DLLName"),
                                                       child?.GetValueType("DLLName") ?? RegistryValueType.None);
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var path = $@"{notifyPath}\{name}";
                var (image, args) = CommandLine.Split(raw, fs);

                found.Add(new StartupItem
                {
                    Id = Identity.Compute(Id, null, path, name),
                    ProviderId = Id,
                    Location = path,
                    Name = name,
                    RawValue = raw,
                    Category = Category,
                    ImagePath = image,
                    Arguments = args,
                    State = EnableState.Enabled,
                    StateSource = "ValuePresent",
                    Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ValueName"] = "DLLName",
                    },
                });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, notifyPath, ex.Message));
        }

        return found;
    }
}
