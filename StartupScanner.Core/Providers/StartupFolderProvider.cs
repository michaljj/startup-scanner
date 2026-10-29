using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Writing;

namespace StartupScanner.Core.Providers;

public sealed class StartupFolderProvider(IFileSystem fs, IRegistry registry, IShellLinkResolver links)
    : IStartupProvider
{
    private const string StartupTail = @"Microsoft\Windows\Start Menu\Programs\Startup";
    private readonly StartupApprovedReader _approved = new(registry);

    public string Id => "startup.folder";
    public StartupCategory Category => StartupCategory.StartupFolder;

    public static string UserStartupPath(string appDataRoaming)
        => Path.Combine(appDataRoaming, StartupTail);

    public static string CommonStartupPath(IFileSystem fs)
        => Path.Combine(fs.ExpandEnvironment("%ProgramData%"), StartupTail);

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        var folders = new List<(string Path, bool Common, string? Sid)>
        {
            (UserStartupPath(fs.ExpandEnvironment("%APPDATA%")), false, ctx.UserSid),
            (CommonStartupPath(fs), true, null),
        };

        foreach (var profile in ctx.Profiles)
            folders.Add((Path.Combine(profile.ProfilePath, @"AppData\Roaming", StartupTail), false, profile.Sid));

        foreach (var (folder, common, sid) in folders)
        {
            foreach (var item in ScanFolder(folder, common, sid, ctx)) yield return item;
        }
    }

    private IEnumerable<StartupItem> ScanFolder(string folder, bool common, string? sid, ScanContext ctx)
    {
        List<string> files;
        try
        {
            if (!fs.DirectoryExists(folder)) return Array.Empty<StartupItem>();
            files = fs.EnumerateFiles(folder)
                      .Where(f => !string.Equals(Path.GetFileName(f), "desktop.ini", StringComparison.OrdinalIgnoreCase))
                      .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            ctx.Errors.Add(new ScanError(Id, folder, ex.Message));
            return Array.Empty<StartupItem>();
        }

        return files.Select(file => Build(folder, file, common, sid, ctx)).ToList();
    }

    private StartupItem Build(string folder, string file, bool common, string? sid, ScanContext ctx)
    {
        var name = Path.GetFileName(file);
        var isShortcut = name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
        var target = isShortcut ? links.Resolve(file) : null;

        // The StartupApproved state for another profile's folder lives in that profile's hive, which
        // may not be mounted during this pass; the read then reports "no entry", i.e. enabled.
        var approvedRoot = common ? "HKLM" : sid is null ? ctx.UserRoot : $@"HKU\{sid}";
        var (state, source) = _approved.ReadFolder(approvedRoot, name);

        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CommonFolder"] = common.ToString(),
            ["FilePath"] = file,
            ["WorkingDirectory"] = target?.WorkingDirectory ?? "",
            ["IconLocation"] = target?.IconLocation ?? "",
            ["LinkTarget"] = target?.TargetPath ?? "",
            ["ApprovedRoot"] = approvedRoot,
        };

        return new StartupItem
        {
            Id = Identity.Compute(Id, sid, folder, name),
            ProviderId = Id,
            Location = folder,
            Name = name,
            RawValue = target is null ? file : $"{target.TargetPath} {target.Arguments}".TrimEnd(),
            Category = Category,
            ImagePath = isShortcut ? target?.TargetPath : file,
            Arguments = string.IsNullOrWhiteSpace(target?.Arguments) ? null : target!.Arguments,
            State = state,
            StateSource = source,
            UserSid = sid,
            PartiallyRead = isShortcut && target is null,
            Extra = extra,
        };
    }
}
