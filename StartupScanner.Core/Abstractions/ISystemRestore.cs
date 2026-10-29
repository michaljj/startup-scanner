namespace StartupScanner.Core.Abstractions;

public interface ISystemRestore
{
    /// <summary>False with a populated error when System Protection is disabled or the call fails.</summary>
    bool TryCreateRestorePoint(string description, out string? error);
}

public interface IRestorePointPrompt
{
    /// <summary>Returns true to continue writing without restore-point protection.</summary>
    bool ContinueWithoutRestorePoint(string reason);
}
