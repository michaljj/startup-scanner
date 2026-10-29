using System.Security.AccessControl;
using System.Security.Principal;
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Windows;

public sealed class WindowsFileOwnerReader : IFileOwnerReader
{
    public string? GetOwner(string path)
    {
        try
        {
            var owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(NTAccount));
            return owner?.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                     or IdentityNotMappedException or PrivilegeNotHeldException)
        {
            return null;
        }
    }
}
