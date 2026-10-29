using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;

namespace StartupScanner.Core.Writing;

/// <summary>
/// Reads the StartupApproved state that Task Manager and msconfig write, for both Run keys and
/// Startup folder shortcuts.
/// </summary>
public sealed class StartupApprovedReader(IRegistry registry)
{
    private const string Tail = @"Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>
    /// Full path of the StartupApproved key for a hive root. Root is either "HKLM" or a user hive
    /// root such as "HKCU" or "HKU\S-1-5-21-…".
    /// </summary>
    public static string ApprovedKeyPath(string root, string approvedSubKey)
        => root.Equals("HKLM", StringComparison.OrdinalIgnoreCase)
            ? $@"HKLM\SOFTWARE\{Tail}\{approvedSubKey}"
            : $@"{root}\Software\{Tail}\{approvedSubKey}";

    public (EnableState State, string Source) ReadRun(RunKeyDefinition def, ScanContext ctx, string valueName)
    {
        if (def.ApprovedSubKey is null) return (EnableState.Enabled, "NoStartupApprovedCoverage");
        var root = def.RootToken == "HKLM" ? "HKLM" : ctx.UserRoot;
        return Read(ApprovedKeyPath(root, def.ApprovedSubKey), def.View, valueName);
    }

    /// <param name="approvedRoot">"HKLM" for the common folder, otherwise the owning hive root.</param>
    public (EnableState State, string Source) ReadFolder(string approvedRoot, string fileName)
        => Read(ApprovedKeyPath(approvedRoot, "StartupFolder"), RegistryViewKind.Default, fileName);

    public (EnableState State, string Source) Read(string approvedKeyPath, RegistryViewKind view, string valueName)
    {
        try
        {
            using var key = registry.OpenKey(approvedKeyPath, view);
            if (key?.GetValue(valueName) is byte[] blob)
                return (StartupApprovedBlob.IsEnabled(blob) ? EnableState.Enabled : EnableState.Disabled,
                        "StartupApproved");
        }
        catch (UnauthorizedAccessException)
        {
            return (EnableState.Unknown, "StartupApproved (access denied)");
        }

        return (EnableState.Enabled, "Default (no StartupApproved entry)");
    }
}
