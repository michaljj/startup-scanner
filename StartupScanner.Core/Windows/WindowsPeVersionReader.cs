using System.Diagnostics;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Windows;

public sealed class WindowsPeVersionReader : IPeVersionReader
{
    public PeVersion? Read(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (info.CompanyName is null && info.ProductName is null
                && info.FileDescription is null && info.FileVersion is null)
                return null;

            return new PeVersion(info.CompanyName, info.ProductName, info.FileDescription, info.FileVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
