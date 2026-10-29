namespace StartupScanner.Core.Abstractions;

public sealed record ShortcutTarget(string TargetPath, string Arguments, string WorkingDirectory, string IconLocation);

public interface IShellLinkResolver
{
    /// <summary>Resolves a .lnk via IShellLink. Null when the file is not a readable shortcut.</summary>
    ShortcutTarget? Resolve(string lnkPath);
}
