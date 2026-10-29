using StartupScanner.Core.Models;

namespace StartupScanner.Core.Providers;

public interface IStartupProvider
{
    /// <summary>Stable dotted id, e.g. "registry.run". Persisted in snapshots and journal lines.</summary>
    string Id { get; }

    StartupCategory Category { get; }

    /// <summary>
    /// Enumerates this provider's locations. Per-entry failures are added to ctx.Errors and the
    /// remaining entries still yield; only a catastrophic failure should throw.
    /// </summary>
    IEnumerable<StartupItem> Scan(ScanContext ctx);
}
