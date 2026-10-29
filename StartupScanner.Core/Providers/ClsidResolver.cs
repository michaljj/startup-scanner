using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Providers;

/// <summary>Resolves a CLSID to the DLL that implements it, checking the user hive first.</summary>
public sealed class ClsidResolver(IRegistry registry)
{
    public string? ResolveInprocServer(string clsid, string userRoot)
    {
        foreach (var path in new[]
                 {
                     $@"{userRoot}\Software\Classes\CLSID\{clsid}\InprocServer32",
                     $@"HKLM\SOFTWARE\Classes\CLSID\{clsid}\InprocServer32",
                 })
        {
            try
            {
                using var key = registry.OpenKey(path);
                // The implementing DLL is the key's default (unnamed) value.
                var value = RegistryValueCodec.ToDisplay(key?.GetValue(""), key?.GetValueType("") ?? RegistryValueType.None);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Fall through to the next hive.
            }
        }

        return null;
    }
}
