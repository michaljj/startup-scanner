using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Writing;

public interface IRestorePointGate
{
    /// <summary>
    /// Ensures a restore point exists for this session, asking the user whether to continue if one
    /// cannot be created. False means the caller must not write.
    /// </summary>
    bool Ensure(out string? error);
}

public sealed class RestorePointGate(
    ISystemRestore restore,
    IRestorePointPrompt prompt,
    string description = "StartupScanner: before first change") : IRestorePointGate
{
    private bool _satisfied;

    public bool Ensure(out string? error)
    {
        error = null;
        if (_satisfied) return true;

        if (restore.TryCreateRestorePoint(description, out var failure))
        {
            _satisfied = true;
            return true;
        }

        var reason = failure ?? "Creating a system restore point failed.";
        if (prompt.ContinueWithoutRestorePoint(reason))
        {
            _satisfied = true;
            return true;
        }

        // Not memoised: the user may enable System Protection and try again.
        error = reason;
        return false;
    }
}
