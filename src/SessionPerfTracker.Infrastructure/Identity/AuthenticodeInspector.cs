using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace SessionPerfTracker.Infrastructure.Identity;

internal static class SignerStatusText
{
    public const string Signed = "Signed";
    public const string SignedCatalog = "Signed (Windows catalog)";
    public const string SignedUntrusted = "Signed (untrusted certificate)";
    public const string SignedExpired = "Signed (expired certificate)";
    public const string Invalid = "Signature invalid";
    public const string Unsigned = "Unsigned";
    public const string Unknown = "Unknown";
}

internal sealed record AuthenticodeResult(string Status, string? Subject);

/// <summary>
/// Real Authenticode verification via WinVerifyTrust plus a Windows catalog lookup for system
/// binaries that carry no embedded signature. No network revocation checks, so it never stalls.
/// </summary>
internal static class AuthenticodeInspector
{
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x00000010;
    private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;

    private const uint TrustENoSignature = 0x800B0100;
    private const uint TrustESubjectFormUnknown = 0x800B0003;
    private const uint TrustEProviderUnknown = 0x800B0001;
    private const uint TrustEBadDigest = 0x80096010;
    private const uint TrustEExplicitDistrust = 0x800B0111;
    private const uint CertEExpired = 0x800B0101;
    private const uint CertEUntrustedRoot = 0x800B0109;
    private const uint CertEChaining = 0x800B010A;
    private const uint CertEUntrustedTestRoot = 0x800B010D;

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static AuthenticodeResult Inspect(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new AuthenticodeResult(SignerStatusText.Unknown, null);
        }

        uint result;
        try
        {
            result = VerifyEmbedded(path);
        }
        catch
        {
            return new AuthenticodeResult(SignerStatusText.Unknown, null);
        }

        switch (result)
        {
            case 0:
                return new AuthenticodeResult(SignerStatusText.Signed, TryReadSubject(path));
            case TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown:
                return IsCatalogSigned(path)
                    ? new AuthenticodeResult(SignerStatusText.SignedCatalog, "Microsoft Windows")
                    : new AuthenticodeResult(SignerStatusText.Unsigned, null);
            case CertEUntrustedRoot or CertEChaining or CertEUntrustedTestRoot:
                return new AuthenticodeResult(SignerStatusText.SignedUntrusted, TryReadSubject(path));
            case CertEExpired:
                return new AuthenticodeResult(SignerStatusText.SignedExpired, TryReadSubject(path));
            case TrustEBadDigest or TrustEExplicitDistrust:
                return new AuthenticodeResult(SignerStatusText.Invalid, TryReadSubject(path));
            default:
                return new AuthenticodeResult(SignerStatusText.Unknown, TryReadSubject(path));
        }
    }

    private static uint VerifyEmbedded(string path)
    {
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = path
        };
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeNone,
                UnionChoice = WtdChoiceFile,
                File = fileInfoPointer,
                StateAction = WtdStateActionVerify,
                ProvFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval
            };

            var action = GenericVerifyV2;
            var status = unchecked((uint)WinVerifyTrust(IntPtr.Zero, ref action, ref data));
            data.StateAction = WtdStateActionClose;
            _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return status;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    private static bool IsCatalogSigned(string path)
    {
        foreach (var algorithm in new[] { "SHA256", null })
        {
            IntPtr context = IntPtr.Zero;
            try
            {
                var acquired = algorithm is null
                    ? CryptCATAdminAcquireContext(out context, IntPtr.Zero, 0)
                    : CryptCATAdminAcquireContext2(out context, IntPtr.Zero, algorithm, IntPtr.Zero, 0);
                if (!acquired || context == IntPtr.Zero)
                {
                    continue;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var handle = stream.SafeFileHandle.DangerousGetHandle();
                uint hashSize = 0;
                var sized = algorithm is null
                    ? CryptCATAdminCalcHashFromFileHandle(handle, ref hashSize, null, 0)
                    : CryptCATAdminCalcHashFromFileHandle2(context, handle, ref hashSize, null, 0);
                if (!sized && hashSize == 0)
                {
                    continue;
                }

                var hash = new byte[hashSize];
                var hashed = algorithm is null
                    ? CryptCATAdminCalcHashFromFileHandle(handle, ref hashSize, hash, 0)
                    : CryptCATAdminCalcHashFromFileHandle2(context, handle, ref hashSize, hash, 0);
                if (!hashed)
                {
                    continue;
                }

                var catalog = CryptCATAdminEnumCatalogFromHash(context, hash, hashSize, 0, IntPtr.Zero);
                if (catalog != IntPtr.Zero)
                {
                    _ = CryptCATAdminReleaseCatalogContext(context, catalog, 0);
                    return true;
                }
            }
            catch
            {
            }
            finally
            {
                if (context != IntPtr.Zero)
                {
                    _ = CryptCATAdminReleaseContext(context, 0);
                }
            }
        }

        return false;
    }

    private static string? TryReadSubject(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminAcquireContext(out IntPtr catAdmin, IntPtr subsystem, uint flags);

    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptCATAdminAcquireContext2(
        out IntPtr catAdmin,
        IntPtr subsystem,
        string hashAlgorithm,
        IntPtr strongHashPolicy,
        uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle(IntPtr file, ref uint hashSize, byte[]? hash, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(
        IntPtr catAdmin,
        IntPtr file,
        ref uint hashSize,
        byte[]? hash,
        uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(
        IntPtr catAdmin,
        byte[] hash,
        uint hashSize,
        uint flags,
        IntPtr previousCatalog);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr catAdmin, IntPtr catalog, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr catAdmin, uint flags);
}
