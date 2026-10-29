using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Providers;

/// <summary>
/// AppX startup tasks and their per-user enable state. The state layout is package-private and has
/// changed between Windows releases, so anything unexpected is reported read-only rather than
/// interpreted.
/// </summary>
public sealed class AppxStartupTaskProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    public const string SystemAppDataSubKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    public const int EnabledState = 2;
    public const int DisabledState = 1;

    public string Id => "appx-startup";
    public StartupCategory Category => StartupCategory.AppxStartupTask;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        var rootPath = $@"{ctx.UserRoot}\{SystemAppDataSubKey}";
        IReadOnlyList<string> packages;

        try
        {
            using var root = registry.OpenKey(rootPath);
            if (root is null) yield break;
            packages = root.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, rootPath, ex.Message));
            yield break;
        }

        foreach (var package in packages)
            foreach (var item in ScanPackage(rootPath, package, ctx))
                yield return item;
    }

    private IReadOnlyList<StartupItem> ScanPackage(string rootPath, string package, ScanContext ctx)
    {
        var packagePath = $@"{rootPath}\{package}";
        var found = new List<StartupItem>();

        try
        {
            using var packageKey = registry.OpenKey(packagePath);
            if (packageKey is null) return found;

            foreach (var taskId in packageKey.GetSubKeyNames())
            {
                using var taskKey = packageKey.OpenSubKey(taskId);
                var stateValue = taskKey?.GetValue("State");
                if (stateValue is null) continue;   // not a startup task

                var taskPath = $@"{packagePath}\{taskId}";
                var recognised = stateValue is int;
                var state = recognised
                    ? (int)stateValue >= EnabledState ? EnableState.Enabled : EnableState.Disabled
                    : EnableState.Unknown;

                found.Add(new StartupItem
                {
                    Id = Identity.Compute(Id, ctx.UserSid, taskPath, taskId),
                    ProviderId = Id,
                    Location = taskPath,
                    Name = taskId,
                    RawValue = package,
                    Category = Category,
                    State = state,
                    StateSource = recognised ? $"Appx.State={stateValue}" : "Appx.State (unrecognised format)",
                    UserSid = ctx.UserSid,
                    PartiallyRead = !recognised,
                    Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["PackageFamilyName"] = package,
                        ["StateRecognised"] = recognised.ToString(),
                    },
                });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, packagePath, ex.Message));
        }

        return found;
    }
}
