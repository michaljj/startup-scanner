using System.Security.Cryptography;
using System.Text;

namespace StartupScanner.Core.Models;

public static class Identity
{
    /// <summary>
    /// Stable item identity. RawValue is excluded on purpose: a Run key whose command line
    /// changes must report as Modified rather than as a Removed plus an Added.
    /// </summary>
    public static string Compute(string providerId, string? userSid, string location, string name)
    {
        var payload = $"{providerId}|{userSid}|{location}|{name}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
