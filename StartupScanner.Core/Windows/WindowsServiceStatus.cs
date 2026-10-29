using System.Diagnostics;
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Windows;

/// <summary>
/// Queries a service's run state with sc.exe rather than ServiceController, so Core needs no
/// System.ServiceProcess dependency for one read-only detail-panel field.
/// </summary>
public sealed class WindowsServiceStatus : IServiceStatus
{
    public string? GetStatus(string serviceName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("sc.exe", $"query \"{serviceName}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            if (output.Contains("RUNNING", StringComparison.Ordinal)) return "Running";
            if (output.Contains("STOPPED", StringComparison.Ordinal)) return "Stopped";
            if (output.Contains("START_PENDING", StringComparison.Ordinal)) return "Starting";
            if (output.Contains("STOP_PENDING", StringComparison.Ordinal)) return "Stopping";
            if (output.Contains("PAUSED", StringComparison.Ordinal)) return "Paused";
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
