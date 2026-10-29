using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Providers;

public sealed class ServiceProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    public const string ServicesKey = @"HKLM\SYSTEM\CurrentControlSet\Services";

    private static readonly string[] Copied =
        { "Start", "Type", "ObjectName", "DisplayName", "Description", "DependOnService", "Group", "ImagePath" };

    public string Id => "service";
    public StartupCategory Category => StartupCategory.Service;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        IReadOnlyList<string> names;
        try
        {
            using var root = registry.OpenKey(ServicesKey);
            if (root is null) yield break;
            names = root.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, ServicesKey, ex.Message));
            yield break;
        }

        foreach (var name in names)
        {
            var item = TryBuild(name, ctx);
            if (item is not null) yield return item;
        }
    }

    private StartupItem? TryBuild(string name, ScanContext ctx)
    {
        var path = $@"{ServicesKey}\{name}";
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        object? start;

        try
        {
            using var key = registry.OpenKey(path);
            if (key is null) return null;

            foreach (var valueName in Copied)
                extra[valueName] = RegistryValueCodec.ToDisplay(key.GetValue(valueName), key.GetValueType(valueName));

            start = key.GetValue("Start");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, path, ex.Message));
            return null;
        }

        var imagePathRaw = extra["ImagePath"];
        var (image, args) = CommandLine.Split(imagePathRaw, fs);
        var typeValue = int.TryParse(extra["Type"], out var t) ? t : 0;
        var category = (typeValue & 0x0F) != 0 && typeValue != 0 ? StartupCategory.Driver : StartupCategory.Service;

        var state = start is null ? EnableState.Unknown
                  : Convert.ToInt32(start) == 4 ? EnableState.Disabled
                  : EnableState.Enabled;

        return new StartupItem
        {
            Id = Identity.Compute(Id, null, path, name),
            ProviderId = Id,
            Location = path,
            Name = name,
            RawValue = imagePathRaw,
            Category = category,
            ImagePath = image,
            Arguments = args,
            State = state,
            StateSource = start is null ? "Service.Start missing" : $"Service.Start={Convert.ToInt32(start)}",
            PartiallyRead = start is null,
            Extra = extra,
        };
    }
}
