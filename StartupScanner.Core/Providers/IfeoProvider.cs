using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Providers;

public sealed class IfeoProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    private const string IfeoKey = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    private static readonly string[] Interesting = { "Debugger", "VerifierDlls", "GlobalFlag" };

    private static readonly RegistryViewKind[] Views = { RegistryViewKind.Registry64, RegistryViewKind.Registry32 };

    public string Id => "ifeo";
    public StartupCategory Category => StartupCategory.Ifeo;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        foreach (var view in Views)
            foreach (var item in ScanView(view, ctx))
                yield return item;
    }

    private IReadOnlyList<StartupItem> ScanView(RegistryViewKind view, ScanContext ctx)
    {
        var found = new List<StartupItem>();

        try
        {
            using var root = registry.OpenKey(IfeoKey, view);
            if (root is null) return found;

            foreach (var image in root.GetSubKeyNames())
            {
                var path = $@"{IfeoKey}\{image}";
                using var key = root.OpenSubKey(image);
                if (key is null) continue;

                foreach (var valueName in Interesting)
                {
                    var raw = RegistryValueCodec.ToDisplay(key.GetValue(valueName), key.GetValueType(valueName));
                    if (string.IsNullOrWhiteSpace(raw)) continue;

                    var name = $"{image}:{valueName}";
                    var (imagePath, args) = CommandLine.Split(raw, fs);

                    found.Add(new StartupItem
                    {
                        Id = Identity.Compute(Id, null, path, name),
                        ProviderId = Id,
                        Location = path,
                        Name = name,
                        RawValue = raw,
                        Category = Category,
                        ImagePath = imagePath,
                        Arguments = args,
                        State = EnableState.Enabled,
                        StateSource = "ValuePresent",
                        Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["TargetImage"] = image,
                            ["ValueName"] = valueName,
                            ["View"] = view.ToString(),
                        },
                    });
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, IfeoKey, ex.Message));
        }

        return found;
    }
}
