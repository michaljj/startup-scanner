using StartupScanner.Core.Models;

namespace StartupScanner.Core.Abstractions;

public interface IPeVersionReader
{
    /// <summary>PE version block, or null when the file has none or cannot be read.</summary>
    PeVersion? Read(string path);
}

public interface IAuthenticode
{
    /// <summary>
    /// Authenticode verification with revocation network retrieval disabled — CRL and OCSP fetches
    /// would stall enrichment for tens of seconds on a slow or filtered network.
    /// </summary>
    SignatureFacts Verify(string path);
}

public interface IFileOwnerReader
{
    string? GetOwner(string path);
}

public interface IServiceStatus
{
    /// <summary>Current run state ("Running", "Stopped", …), or null when it cannot be queried.</summary>
    string? GetStatus(string serviceName);
}
