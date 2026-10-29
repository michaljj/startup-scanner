using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Providers;

public sealed class ActiveSetupProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    private const string MachineKey = @"HKLM\SOFTWARE\Microsoft\Active Setup\Installed Components";
    private const string UserSubKey = @"Software\Microsoft\Active Setup\Installed Components";

    public string Id => "active-setup";
    public StartupCategory Category => StartupCategory.ActiveSetup;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        IReadOnlyList<string> components;
        try
        {
            using var root = registry.OpenKey(MachineKey);
            if (root is null) yield break;
            components = root.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, MachineKey, ex.Message));
            yield break;
        }

        foreach (var component in components)
        {
            var item = TryBuild(component, ctx);
            if (item is not null) yield return item;
        }
    }

    private StartupItem? TryBuild(string component, ScanContext ctx)
    {
        var path = $@"{MachineKey}\{component}";
        string stubPath, machineVersion, componentId, isInstalled;

        try
        {
            using var key = registry.OpenKey(path);
            if (key is null) return null;

            stubPath = Text(key, "StubPath");
            if (string.IsNullOrWhiteSpace(stubPath)) return null;   // nothing runs at logon

            machineVersion = Text(key, "Version");
            componentId = Text(key, "ComponentID");
            isInstalled = Text(key, "IsInstalled");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, path, ex.Message));
            return null;
        }

        var userVersion = UserStamp(component, ctx);
        var (image, args) = CommandLine.Split(stubPath, fs);

        return new StartupItem
        {
            Id = Identity.Compute(Id, null, path, component),
            ProviderId = Id,
            Location = path,
            Name = component,
            RawValue = stubPath,
            Category = Category,
            ImagePath = image,
            Arguments = args,
            State = EnableState.Enabled,
            StateSource = "ValuePresent",
            Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["MachineVersion"] = machineVersion,
                ["UserVersion"] = userVersion,
                ["ComponentId"] = componentId,
                ["IsInstalled"] = isInstalled,
                // The stub runs while the per-user stamp is behind the machine stamp.
                ["WillRunAtNextLogon"] = (!string.Equals(machineVersion, userVersion, StringComparison.OrdinalIgnoreCase)).ToString(),
                ["ValueName"] = "StubPath",
            },
        };
    }

    private string UserStamp(string component, ScanContext ctx)
    {
        try
        {
            using var key = registry.OpenKey($@"{ctx.UserRoot}\{UserSubKey}\{component}");
            return Text(key, "Version");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, $@"{ctx.UserRoot}\{UserSubKey}\{component}", ex.Message));
            return "";
        }
    }

    private static string Text(IRegistryKey? key, string name)
        => key is null ? "" : RegistryValueCodec.ToDisplay(key.GetValue(name), key.GetValueType(name));
}
