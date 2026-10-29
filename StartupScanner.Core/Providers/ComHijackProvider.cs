using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Providers;

/// <summary>
/// Per-user CLSID registrations that shadow a machine one. A per-user key wins over HKLM, so this
/// silently replaces a system COM object; a per-user CLSID with no machine counterpart is an
/// ordinary registration and is not reported.
/// </summary>
public sealed class ComHijackProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    private const string UserClsidSubKey = @"Software\Classes\CLSID";
    private const string MachineClsidKey = @"HKLM\SOFTWARE\Classes\CLSID";

    public string Id => "com-hijack";
    public StartupCategory Category => StartupCategory.ComHijack;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        var rootPath = $@"{ctx.UserRoot}\{UserClsidSubKey}";
        IReadOnlyList<string> clsids;

        try
        {
            using var root = registry.OpenKey(rootPath);
            if (root is null) yield break;
            clsids = root.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, rootPath, ex.Message));
            yield break;
        }

        foreach (var clsid in clsids)
        {
            var item = TryBuild(rootPath, clsid, ctx);
            if (item is not null) yield return item;
        }
    }

    private StartupItem? TryBuild(string rootPath, string clsid, ScanContext ctx)
    {
        var clsidPath = $@"{rootPath}\{clsid}";
        string valueName, raw, location;

        try
        {
            // Opened first so that an unreadable CLSID key is reported rather than silently
            // skipped when only the InprocServer32 subkey happens to be readable.
            using var key = registry.OpenKey(clsidPath);
            using var inproc = registry.OpenKey($@"{clsidPath}\InprocServer32");
            var inprocValue = Value(inproc, "");

            if (inprocValue.Length > 0)
            {
                valueName = "InprocServer32";
                raw = inprocValue;
                location = $@"{clsidPath}\InprocServer32";
            }
            else
            {
                var treatAs = Value(key, "TreatAs");
                if (treatAs.Length == 0) return null;

                valueName = "TreatAs";
                raw = treatAs;
                location = clsidPath;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, clsidPath, ex.Message));
            return null;
        }

        var machine = MachineRegistration(clsid);
        if (machine is null) return null;   // not shadowing anything

        var (image, args) = CommandLine.Split(raw, fs);

        return new StartupItem
        {
            Id = Identity.Compute(Id, ctx.UserSid, location, clsid),
            ProviderId = Id,
            Location = location,
            Name = clsid,
            RawValue = raw,
            Category = Category,
            ImagePath = valueName == "InprocServer32" ? image : null,
            Arguments = valueName == "InprocServer32" ? args : null,
            State = EnableState.Enabled,
            StateSource = "ValuePresent",
            UserSid = ctx.UserSid,
            Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Clsid"] = clsid,
                ["ValueName"] = valueName,
                ["MachineRegistration"] = machine,
            },
        };
    }

    private string? MachineRegistration(string clsid)
    {
        try
        {
            using var key = registry.OpenKey($@"{MachineClsidKey}\{clsid}\InprocServer32");
            var value = Value(key, "");
            return value.Length > 0 ? value : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string Value(IRegistryKey? key, string name)
        => key is null ? "" : RegistryValueCodec.ToDisplay(key.GetValue(name), key.GetValueType(name));
}
