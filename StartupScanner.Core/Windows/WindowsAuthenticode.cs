using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Windows;

/// <summary>
/// Authenticode verification with all network retrieval disabled. Most Windows binaries are
/// catalog-signed rather than embedded-signed, so a TRUST_E_NOSIGNATURE result from file
/// verification is followed by a catalog lookup before concluding the file is unsigned.
/// </summary>
public sealed class WindowsAuthenticode : IAuthenticode
{
    public SignatureFacts Verify(string path)
    {
        var embedded = VerifyFile(path);

        if (HasNoSignature(embedded))
        {
            var (catalogResult, catalogFound) = VerifyCatalog(path);
            if (catalogFound) return Describe(path, catalogResult, isCatalog: true);

            return new SignatureFacts
            {
                Status = SignatureStatus.Unsigned,
                StatusMessage = "No embedded signature and no catalog entry.",
            };
        }

        return Describe(path, embedded, isCatalog: false);
    }

    private static SignatureFacts Describe(string path, int result, bool isCatalog)
    {
        var chain = new List<string>();
        string? signer = null;
        DateTime? timestamp = null;
        var microsoft = false;

        try
        {
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            signer = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

            using var builder = new X509Chain();
            builder.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;   // no network, by design
            builder.Build(certificate);

            foreach (var element in builder.ChainElements)
                chain.Add(element.Certificate.Subject);

            microsoft = chain.Any(subject => subject.Contains("Microsoft", StringComparison.OrdinalIgnoreCase));
            timestamp = certificate.NotBefore.ToUniversalTime();
        }
        catch (CryptographicException)
        {
            // Catalog-signed files carry no embedded certificate; the catalog verified it instead.
            if (isCatalog)
            {
                signer = "Microsoft Windows (catalog)";
                microsoft = true;
            }
        }

        return new SignatureFacts
        {
            Status = result == 0 ? SignatureStatus.Valid
                   : HasNoSignature(result) ? SignatureStatus.Unsigned
                   : SignatureStatus.Invalid,
            SignerName = signer,
            Chain = chain,
            TimestampUtc = timestamp,
            IsCatalogSigned = isCatalog,
            IsMicrosoftSigned = microsoft,
            StatusMessage = result == 0 ? null : $"WinVerifyTrust returned 0x{result:X8}.",
        };
    }

    private static int VerifyFile(string path)
    {
        var fileInfo = new WintrustFileInfo
        {
            StructSize = Marshal.SizeOf<WintrustFileInfo>(),
            FilePath = Marshal.StringToCoTaskMemUni(path),
        };
        var filePointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WintrustFileInfo>());

        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, false);

            var data = NewTrustData();
            data.UnionChoice = WtdChoiceFile;
            data.UnionPointer = filePointer;

            return CallWinVerifyTrust(ref data);
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileInfo.FilePath);
            Marshal.FreeCoTaskMem(filePointer);
        }
    }

    private static (int Result, bool Found) VerifyCatalog(string path)
    {
        var context = IntPtr.Zero;
        var fileHandle = IntPtr.Zero;

        try
        {
            if (!CryptCATAdminAcquireContext2(out context, IntPtr.Zero, "SHA256", IntPtr.Zero, 0))
                return (TrustENoSignature, false);

            fileHandle = CreateFile(path, GenericRead, FileShareRead, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (fileHandle == InvalidHandle) return (TrustENoSignature, false);

            var hashLength = 0;
            CryptCATAdminCalcHashFromFileHandle2(context, fileHandle, ref hashLength, IntPtr.Zero, 0);
            if (hashLength == 0) return (TrustENoSignature, false);

            var hash = Marshal.AllocCoTaskMem(hashLength);
            try
            {
                if (!CryptCATAdminCalcHashFromFileHandle2(context, fileHandle, ref hashLength, hash, 0))
                    return (TrustENoSignature, false);

                var catalog = CryptCATAdminEnumCatalogFromHash(context, hash, hashLength, 0, IntPtr.Zero);
                if (catalog == IntPtr.Zero) return (TrustENoSignature, false);

                var info = new CatalogInfo { StructSize = Marshal.SizeOf<CatalogInfo>() };
                if (!CryptCATCatalogInfoFromContext(catalog, ref info, 0))
                    return (TrustENoSignature, false);

                return (VerifyAgainstCatalog(path, info.CatalogFile, hash, hashLength), true);
            }
            finally
            {
                Marshal.FreeCoTaskMem(hash);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (TrustENoSignature, false);
        }
        finally
        {
            if (fileHandle != IntPtr.Zero && fileHandle != InvalidHandle) CloseHandle(fileHandle);
            if (context != IntPtr.Zero) CryptCATAdminReleaseContext(context, 0);
        }
    }

    private static int VerifyAgainstCatalog(string path, string catalogFile, IntPtr hash, int hashLength)
    {
        var memberTag = new StringBuilder(hashLength * 2);
        for (var i = 0; i < hashLength; i++) memberTag.Append(Marshal.ReadByte(hash, i).ToString("X2"));

        var catalogInfo = new WintrustCatalogInfo
        {
            StructSize = Marshal.SizeOf<WintrustCatalogInfo>(),
            CatalogFilePath = Marshal.StringToCoTaskMemUni(catalogFile),
            MemberFilePath = Marshal.StringToCoTaskMemUni(path),
            MemberTag = Marshal.StringToCoTaskMemUni(memberTag.ToString()),
        };
        var catalogPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WintrustCatalogInfo>());

        try
        {
            Marshal.StructureToPtr(catalogInfo, catalogPointer, false);

            var data = NewTrustData();
            data.UnionChoice = WtdChoiceCatalog;
            data.UnionPointer = catalogPointer;

            return CallWinVerifyTrust(ref data);
        }
        finally
        {
            Marshal.FreeCoTaskMem(catalogInfo.CatalogFilePath);
            Marshal.FreeCoTaskMem(catalogInfo.MemberFilePath);
            Marshal.FreeCoTaskMem(catalogInfo.MemberTag);
            Marshal.FreeCoTaskMem(catalogPointer);
        }
    }

    private static WintrustData NewTrustData() => new()
    {
        StructSize = Marshal.SizeOf<WintrustData>(),
        UIChoice = WtdUiNone,
        RevocationChecks = WtdRevokeNone,
        StateAction = WtdStateActionVerify,
        // No network retrieval: CRL and OCSP fetches would stall enrichment for tens of seconds.
        ProviderFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval,
    };

    private static int CallWinVerifyTrust(ref WintrustData data)
    {
        var action = ActionGenericVerifyV2;
        var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

        data.StateAction = WtdStateActionClose;
        WinVerifyTrust(IntPtr.Zero, ref action, ref data);

        return result;
    }

    /// <summary>
    /// "Nothing to verify" rather than "verification failed": no signature at all, or a file whose
    /// format no trust provider recognises. Both mean unsigned; only a real verification failure
    /// (a bad digest, an untrusted root) is Invalid.
    /// </summary>
    private static bool HasNoSignature(int result)
        => result is TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown;

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdChoiceCatalog = 2;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x00000010;
    private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private static readonly IntPtr InvalidHandle = new(-1);

    private static Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public int StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustCatalogInfo
    {
        public int StructSize;
        public uint CatalogVersion;
        public IntPtr CatalogFilePath;
        public IntPtr MemberTag;
        public IntPtr MemberFilePath;
        public IntPtr MemberFileHandle;
        public IntPtr CalculatedFileHash;
        public uint CalculatedFileHashSize;
        public IntPtr CatalogContext;
        public IntPtr CatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public int StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UIChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr UnionPointer;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UIContext;
        public IntPtr SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogInfo
    {
        public int StructSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string CatalogFile;
    }

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WintrustData data);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr subsystem,
        string? hashAlgorithm, IntPtr policy, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, IntPtr file,
        ref int hashSize, IntPtr hash, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr admin, IntPtr hash,
        int hashSize, uint flags, IntPtr previous);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr catalog, ref CatalogInfo info, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
