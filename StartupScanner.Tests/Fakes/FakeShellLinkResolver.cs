using StartupScanner.Core.Abstractions;

namespace StartupScanner.Tests.Fakes;

public sealed class FakeShellLinkResolver : IShellLinkResolver
{
    private readonly Dictionary<string, ShortcutTarget> _targets = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string lnkPath, ShortcutTarget target) => _targets[lnkPath] = target;

    public ShortcutTarget? Resolve(string lnkPath) => _targets.TryGetValue(lnkPath, out var t) ? t : null;
}
