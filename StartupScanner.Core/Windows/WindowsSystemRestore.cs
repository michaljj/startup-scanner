using System.Management;
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Windows;

public sealed class WindowsSystemRestore : ISystemRestore
{
    private const uint ApplicationInstall = 0;
    private const uint BeginSystemChange = 100;

    public bool TryCreateRestorePoint(string description, out string? error)
    {
        try
        {
            using var restoreClass = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            var parameters = restoreClass.GetMethodParameters("CreateRestorePoint");
            parameters["Description"] = description;
            parameters["RestorePointType"] = ApplicationInstall;
            parameters["EventType"] = BeginSystemChange;

            var result = restoreClass.InvokeMethod("CreateRestorePoint", parameters, null);
            var returnValue = Convert.ToUInt32(result?["ReturnValue"] ?? 1u);

            if (returnValue == 0)
            {
                error = null;
                return true;
            }

            error = $"CreateRestorePoint returned {returnValue}. System Protection may be turned off for this drive.";
            return false;
        }
        catch (Exception ex)
        {
            error = $"A system restore point could not be created: {ex.Message}";
            return false;
        }
    }
}
