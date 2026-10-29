using System.Runtime.InteropServices;
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Windows;

/// <summary>
/// Mounts a logged-out profile's NTUSER.DAT under HKU. RegLoadKey needs SeBackupPrivilege and
/// SeRestorePrivilege enabled in the process token, which elevation alone does not do.
/// </summary>
public sealed class WindowsUserHiveLoader : IUserHiveLoader
{
    private const string MountPrefix = "StartupScanner_";

    public MountedHive Load(string sid, string hiveFile)
    {
        if (!File.Exists(hiveFile))
            throw new FileNotFoundException($"Profile hive not found: {hiveFile}", hiveFile);

        EnablePrivilege("SeBackupPrivilege");
        EnablePrivilege("SeRestorePrivilege");

        var mountName = MountPrefix + sid;
        var result = RegLoadKey(HkeyUsers, mountName, hiveFile);
        if (result != 0)
            throw new InvalidOperationException($"RegLoadKey failed with Win32 error {result} for '{hiveFile}'.");

        return new MountedHive($@"HKU\{mountName}", new Unmounter(mountName));
    }

    private sealed class Unmounter(string mountName) : IDisposable
    {
        public void Dispose() => RegUnLoadKey(HkeyUsers, mountName);
    }

    private static void EnablePrivilege(string name)
    {
        var process = GetCurrentProcess();
        if (!OpenProcessToken(process, TokenAdjustPrivileges | TokenQuery, out var token))
            throw new InvalidOperationException($"OpenProcessToken failed: {Marshal.GetLastWin32Error()}");

        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid))
                throw new InvalidOperationException($"LookupPrivilegeValue('{name}') failed: {Marshal.GetLastWin32Error()}");

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled,
            };

            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException($"AdjustTokenPrivileges('{name}') failed: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static readonly IntPtr HkeyUsers = new(unchecked((int)0x80000003));
    private const int TokenAdjustPrivileges = 0x0020;
    private const int TokenQuery = 0x0008;
    private const int SePrivilegeEnabled = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public int PrivilegeCount;
        public Luid Luid;
        public int Attributes;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegLoadKey(IntPtr hKey, string lpSubKey, string lpFile);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegUnLoadKey(IntPtr hKey, string lpSubKey);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAll,
        ref TokenPrivileges newState, int bufferLength, IntPtr previousState, IntPtr returnLength);
}
